using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// <c>kcap grok-bot watch</c>: polls the Grok Bot gateway on its cloud computer and records every chat as
/// Capacitor sessions. The gateway lists each chat as an agent: a Bot's own thread, and every new chat as a
/// group, so groups are read like any other. One instance per computer, held by a lock file; delivery state advances
/// only after the server accepts a call, so a restart resends nothing and loses nothing.
/// </summary>
sealed class GrokBotWatchCommand(ConfigRoot config, ProfileContext profiles, UserHome home, ICapacitorHttpClient http, TimeProvider time) {
    static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan DefaultGap      = TimeSpan.FromHours(2);
    static readonly TimeSpan BackoffCeiling  = TimeSpan.FromMinutes(5);

    // The fingerprint only spares reads; a chat is re-read at least this often whatever it says, so a gateway
    // field that stops moving delays capture instead of stopping it.
    static readonly TimeSpan FullReadInterval = TimeSpan.FromMinutes(1);

    // A thread longer than this many tail pages is not paged further back in one cycle.
    const int MaxPagesPerCycle = 50;

    public async Task<int> Handle(string[] args) {
        if (args.Length < 2 || args[1] != "watch") {
            await Console.Error.WriteLineAsync("Usage: kcap grok-bot watch [--once] [--dry-run] [--gateway <gateway.json>] [--interval <seconds>] [--gap-minutes <n>]");
            return 1;
        }

        var descriptor = Option(args, "--gateway") ?? Path.Join(home.Path, "sand-data", "gateway.json");
        var interval   = int.TryParse(Option(args, "--interval"), out var s) && s > 0 ? TimeSpan.FromSeconds(s) : DefaultInterval;
        var gap        = int.TryParse(Option(args, "--gap-minutes"), out var g) && g > 0 ? TimeSpan.FromMinutes(g) : DefaultGap;
        var once       = args.Contains("--once");
        var dryRun     = args.Contains("--dry-run");

        if (!File.Exists(descriptor)) {
            await Console.Error.WriteLineAsync($"No Grok Bot gateway at {descriptor}. Run this on a Grok Bot cloud computer, or pass --gateway.");
            return 1;
        }

        Directory.CreateDirectory(config.Path("grok-bot"));
        FileStream lockFile;
        try {
            lockFile = new FileStream(config.Path("grok-bot", "watch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        } catch (IOException) {
            await Console.Error.WriteLineAsync("kcap grok-bot watch is already running on this computer.");
            return 1;
        }

        using var _      = lockFile;
        using var cts    = new CancellationTokenSource();
        using var plain  = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var gateway = new GrokBotGateway(descriptor, plain);
        var store   = new GrokBotStateStore(config.Path("grok-bot", "state.json"));
        var states  = store.Load();
        var seen    = new Dictionary<string, (string Fingerprint, DateTimeOffset ReadAt)>(StringComparer.Ordinal);
        var backoff = new GrokBotBackoff(interval, BackoffCeiling);

        Log($"watching {descriptor}, gap {gap.TotalMinutes:0} min, {states.Count} Bot(s) in state");

        do {
            string? failure;
            try {
                failure = await CycleAsync(gateway, store, states, seen, gap, dryRun, cts.Token);
            } catch (OperationCanceledException) when (cts.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                failure = $"cycle failed: {ex.Message}";
            }

            if (failure is null) {
                if (backoff.Succeeded() is > 0 and var ended) Log($"recovered after {ended} failed cycle(s)");
            } else if (backoff.Failed(failure) is { } report) {
                Log($"{report}; retrying with backoff, next in {backoff.NextDelay.TotalSeconds:0}s");
            }

            if (once) return failure is null ? 0 : 1;

            try {
                await Task.Delay(backoff.NextDelay, time, cts.Token);
            } catch (OperationCanceledException) {
                break;
            }
        } while (true);

        return 0;
    }

    /// <summary>One pass over every Bot. Returns why the cycle stopped short, or null when it did not; a
    /// refused call ends the cycle, since a lapsed sign-in or a server outage refuses every Bot alike.</summary>
    async Task<string?> CycleAsync(
            GrokBotGateway                       gateway,
            GrokBotStateStore                    store,
            Dictionary<string, GrokBotBotState>  states,
            Dictionary<string, (string Fingerprint, DateTimeOffset ReadAt)> seen,
            TimeSpan                             gap,
            bool                                 dryRun,
            CancellationToken                    ct
        ) {
        var client = await http.ForBackgroundAsync(ct);

        foreach (var agent in await gateway.ListAgentsAsync(ct)) {
            var state   = states.GetValueOrDefault(agent.Id, GrokBotBotState.Empty);
            var now     = time.GetUtcNow();
            var changed = !seen.TryGetValue(agent.Id, out var last)
                       || last.Fingerprint != agent.Fingerprint
                       || now - last.ReadAt >= FullReadInterval;

            if (!changed && state.Open is null) continue;

            var entries = changed ? await ReadSinceAsync(gateway, agent.Id, state.LastSeq, ct) : [];
            var actions = GrokBotSessionizer.Plan(agent.Id, state, entries, now.ToUnixTimeMilliseconds(), agent.IsRunningTurn, gap);

            if (dryRun) {
                Log($"{agent.Name} ({agent.Id}): {entries.Count} entries read, {actions.Count} call(s) planned");
                foreach (var action in actions) Log("  would " + Describe(action));
                continue;
            }

            foreach (var action in actions) {
                if (await SendAsync(client, action, ct) is { } refused) {
                    seen.Remove(agent.Id);
                    return refused;
                }

                states[agent.Id] = action.After;
                store.Save(states);
            }

            if (changed) seen[agent.Id] = (agent.Fingerprint, now);
        }

        return null;
    }

    /// <summary>The newest page, then earlier pages until one reaches <paramref name="lastSeq"/> or the
    /// gateway stops moving backwards.</summary>
    static async Task<List<GrokBotEntry>> ReadSinceAsync(GrokBotGateway gateway, string agentId, int lastSeq, CancellationToken ct) {
        var (entries, before) = await gateway.TailAsync(agentId, null, ct);
        var all = new List<GrokBotEntry>(entries);

        for (var page = 1; page < MaxPagesPerCycle && before is { } cursor && all.Count > 0 && all.Min(e => e.Seq) > lastSeq + 1; page++) {
            var oldest = all.Min(e => e.Seq);
            var (older, next) = await gateway.TailAsync(agentId, cursor, ct);

            if (older.Count == 0 || older.Min(e => e.Seq) >= oldest) break;

            all.AddRange(older);
            before = next;
        }

        return all;
    }

    /// <summary>Sends one call; returns why the server refused it, or null when it was accepted.</summary>
    async Task<string?> SendAsync(HttpClient client, GrokBotAction action, CancellationToken ct) {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var (route, body) = action switch {
            GrokBotStartSession start => ("hooks/session-start/grok-bot", StartBody(start)),
            GrokBotSendLines lines    => ("hooks/transcript", JsonSerializer.Serialize(new TranscriptBatch {
                SessionId   = lines.SessionId,
                Lines       = lines.Lines,
                LineNumbers = lines.Seqs,
                Vendor      = "grok-bot",
                Strict      = false
            }, CapacitorJsonContext.Default.TranscriptBatch)),
            GrokBotEndSession end     => ("hooks/session-end/grok-bot", new JsonObject {
                ["session_id"] = end.SessionId,
                ["ended_at"]   = Iso(end.EndedAtMs),
                ["reason"]     = "idle"
            }.ToJsonString()),
            _ => throw new InvalidOperationException(action.GetType().Name)
        };

        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostWithRetryAsync($"{baseUrl}/{route}", content, time, ct: ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return "the server refused the sign-in (401); run `kcap login --device` on this computer";

        if (!response.IsSuccessStatusCode)
            return $"{route} answered {(int)response.StatusCode} for session {SessionOf(action)}";

        Log(Describe(action));
        return null;
    }

    static string Describe(GrokBotAction action) => action switch {
        GrokBotStartSession a => $"open session {a.SessionId} at {Iso(a.StartedAtMs)}",
        GrokBotSendLines a    => $"send {a.Lines.Length} entr{(a.Lines.Length == 1 ? "y" : "ies")} (seq {a.Seqs[0]}..{a.Seqs[^1]}) to {a.SessionId}",
        GrokBotEndSession a   => $"end session {a.SessionId} at {Iso(a.EndedAtMs)}",
        _                     => action.GetType().Name
    };

    string StartBody(GrokBotStartSession start) {
        var body = new JsonObject {
            ["session_id"] = start.SessionId,
            ["started_at"] = Iso(start.StartedAtMs),
            ["home_dir"]   = home.Path
        };
        if (profiles.Effective?.DefaultVisibility is { } visibility) body["default_visibility"] = visibility;

        return body.ToJsonString();
    }

    static string SessionOf(GrokBotAction action) => action switch {
        GrokBotStartSession a => a.SessionId,
        GrokBotSendLines a    => a.SessionId,
        GrokBotEndSession a   => a.SessionId,
        _                     => "?"
    };

    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");

    static string? Option(string[] args, string name) {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    void Log(string message) => Console.Error.WriteLine($"{time.GetUtcNow():O} {message}");
}

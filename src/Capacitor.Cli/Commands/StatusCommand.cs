using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

public sealed class StatusCommand(
        DaemonStore store, ProfileContext profiles, ConfigRoot config, TokenStore tokenStore, HarnessRegistry harnesses,
        ICapacitorHttpClient http, NpmRegistryClient npm, MachineAuth machine, TimeProvider time,
        bool? appBundled = null) {

    readonly bool _appBundled = appBundled ?? InstallProvenance.IsAppBundled();

    public async Task<int> HandleAsync(string[] args) {
        var baseUrl = profiles.Resolution.ServerUrl;

        if (args.Contains("--json")) return await WriteJsonAsync(args, baseUrl);

        // Version line reuses UpdateNotice's shared check and marks-reported so the exit footer
        // doesn't double-print; respects the same opt-outs.
        await WriteVersionLineAsync(args);

        // Server. The URL goes out ahead of the probe, which can take its whole timeout: a bare
        // label would say nothing about what is being waited on.
        Console.Write(baseUrl is null ? "  Server:  " : $"  Server:  {baseUrl} ");
        var server = await ProbeServerAsync(http, baseUrl, config, time);
        await Console.Out.WriteLineAsync(FormatReachability(server));

        // Auth
        var auth = await ResolveAuthAsync(tokenStore, machine, profiles.Name, server);
        await Console.Out.WriteLineAsync($"  Auth:    {FormatAuthLine(auth, time.GetUtcNow())}");

        // Hooks
        await Console.Out.WriteAsync("  Hooks:   ");

        var line = BuildHooksStatusLine(harnesses.Select(h => (h.Id, h.Signals.IsWired)));

        await Console.Out.WriteLineAsync(line);

        // Newly-installed-but-unconfigured harnesses. Ledger-independent (a dismissed vendor is
        // still surfaced here) — status always tells the truth, unlike the nudge which respects
        // dismissals. Shares the wired-check with the Hooks line above, so the two never disagree.
        foreach (var h in harnesses) {
            if (!harnesses.Detected(h.Id)) continue;
            if (h.Signals.IsWired) continue;
            await Console.Out.WriteLineAsync(
                $"           {h.Label} installed but kcap not configured — run `{InstallCommandFor(h.Id)}`");
        }

        // Daemon: the per-name PID files `kcap daemon status` reads, so the two agree.
        Console.Write("  Daemon:  ");
        await WriteAgentStatusAsync(store);

        return 0;
    }

    /// <summary>
    /// The same facts the human lines carry, as one document on stdout and nothing else. Every value
    /// comes from the gatherers the text path uses, so the two cannot drift.
    /// </summary>
    async Task<int> WriteJsonAsync(string[] args, string? baseUrl) {
        var (current, advisory, bundled) = await ResolveVersionAsync(args);
        var server = await ProbeServerAsync(http, baseUrl, config, time);
        var auth   = await ResolveAuthAsync(tokenStore, machine, profiles.Name, server);

        // The exit-time footer is the human surface for this, and the payload already carries it.
        if (advisory.Newer) UpdateNotice.MarkReported();

        var payload = BuildPayload(
            profiles.Name, server, auth, current, advisory, bundled,
            [.. harnesses.Select(h => {
                var installed = harnesses.Detected(h.Id);
                var wired     = h.Signals.IsWired;

                return new StatusHarnessJson(h.VendorId, installed, wired, installed && !wired ? InstallCommandFor(h.Id) : null);
            })],
            ReadDaemonEntries(store));

        await Console.Out.WriteLineAsync(StatusJsonRender.Render(payload));

        return 0;
    }

    /// <summary>Pure: the payload from facts already gathered. <c>configured</c> is decided from the
    /// server URL and the auth state alone — reachability is reported, never consulted.</summary>
    internal static StatusJson BuildPayload(
            string profile, ServerReach server, AuthSnapshot auth, string current, UpdateAdvisory advisory, bool bundled,
            IReadOnlyList<StatusHarnessJson> harnesses, IReadOnlyList<DaemonEntry> daemons) {
        var live = daemons.Where(e => e.Alive).ToList();

        return new StatusJson(
            StatusJsonRender.IsConfigured(server.Url, auth.State),
            profile,
            new StatusServerJson(server.Url, server.Url is null ? null : server.Reachable, server.StatusCode),
            new StatusAuthJson(auth.State.Wire, auth.Identity, auth.ExpiresAt),
            new StatusVersionJson(
                current,
                advisory is { Newer: true, Target: { } target } ? target : null,
                advisory.ServerCapped,
                bundled),
            harnesses,
            new StatusDaemonJson(
                live.Count > 0,
                [.. live.Select(e => new StatusDaemonEntryJson(e.Name, e.Pid!.Value))],
                daemons.Count > live.Count));
    }

    /// <summary>Reachability, not authorization: a bearer would turn an unauthenticated-but-running
    /// server into a failure, and this probe reports the connection.</summary>
    internal static async Task<ServerReach> ProbeServerAsync(
            ICapacitorHttpClient http, string? baseUrl, ConfigRoot config, TimeProvider time) {
        if (baseUrl is null) return new ServerReach(null, false, null);

        // Only an answer names the provider. Without one, the last successful discovery on disk
        // stands in, so a server that asks for no auth stays set up through an outage. Discovery's
        // own fallback is not used here: it answers "None" for an unreachable server with an empty
        // token store, which would call a machine nobody set up configured.
        try {
            using var client = http.Anonymous();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var resp = await client.GetAsync($"{baseUrl}/auth/config");

            if (!resp.IsSuccessStatusCode) return new ServerReach(baseUrl, false, (int)resp.StatusCode, LastKnownProvider());

            var announced = await AnnouncedProviderAsync(resp);

            // Status may be the only thing that ever asks this server, so its answer is recorded
            // the way discovery records its own: the fallback above has nothing else to read.
            if (announced is not null) AuthProviderCache.Set(baseUrl, announced, config, time);

            return new ServerReach(baseUrl, true, null, announced ?? LastKnownProvider());
        } catch {
            return new ServerReach(baseUrl, false, null, LastKnownProvider());
        }

        string? LastKnownProvider() => AuthProviderCache.TryGet(baseUrl, config, time);
    }

    /// <summary>Read the way discovery reads it, so both agree on what a server announced: a body
    /// with no provider is <see cref="AuthProvider.None"/>. Null when the body is not that document.</summary>
    static async Task<string?> AnnouncedProviderAsync(HttpResponseMessage resp) {
        try {
            var config = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AuthDiscoveryResponse);

            return config?.Provider ?? AuthProvider.None;
        } catch (Exception e) when (e is JsonException or NotSupportedException) {
            return null;
        }
    }

    internal static string FormatReachability(ServerReach server) =>
        server.Url is null ? "not configured"
        : server.Reachable ? "✓ reachable"
        : server.StatusCode is { } code ? $"✗ HTTP {code}"
        : "✗ unreachable";

    /// <summary>
    /// Decided in the order the credential lane picks a source — no auth, then the machine, then the
    /// token store — and each one REPLACES the ones after it rather than adding to them. With
    /// KCAP_CLIENT_ID/KCAP_CLIENT_SECRET in the environment, MachineAuth.Intended bypasses the token
    /// store entirely, so its state is not what this CLI authenticates with. Reporting both would
    /// show a headless runner as recording as the machine AND not authenticated, contradictory and
    /// with irrelevant remediation.
    /// </summary>
    internal static async Task<AuthSnapshot> ResolveAuthAsync(
            TokenStore tokens, MachineAuth machine, string profile, ServerReach server) {
        if (server.Provider == AuthProvider.None) return new AuthSnapshot(StatusAuthState.NotRequired);

        // Diversion is deliberately raised by EITHER variable, so that a half-configured runner is
        // diagnosed rather than sent to `kcap login` it cannot run. Only both halves authenticate,
        // which is the difference between "records as the machine" and "nothing records".
        if (machine.Diversion is { } diversion)
            return new AuthSnapshot(
                machine.TryRead(out _) is not null ? StatusAuthState.Machine : StatusAuthState.MachineIncomplete,
                MachineLine: diversion);

        // With no server there is nothing to bind a token to, so validity is all that can be said.
        if (server.Url is null) {
            if (await tokens.GetValidTokensForProfileAsync(profile) is { } unbound)
                return new AuthSnapshot(StatusAuthState.Valid, unbound.GitHubUsername, unbound.ExpiresAt);

            return await tokens.LoadForProfileAsync(profile) is { } stored
                ? new AuthSnapshot(StatusAuthState.Expired, stored.GitHubUsername)
                : new AuthSnapshot(StatusAuthState.None);
        }

        // The server-aware accessor, not the profile-only one: a token bound elsewhere is withheld
        // before any request, so reporting it as valid would promise access that never happens.
        var resolved = await tokens.GetValidTokensForServerAsync(profile, server.Url);

        return resolved.Status switch {
            AuthStatus.Ok          => new AuthSnapshot(StatusAuthState.Valid, resolved.Tokens!.GitHubUsername, resolved.Tokens.ExpiresAt),
            AuthStatus.WrongServer => new AuthSnapshot(StatusAuthState.WrongServer, IssuedServerUrl: resolved.IssuedServerUrl),
            AuthStatus.Expired     => new AuthSnapshot(StatusAuthState.Expired, (await tokens.LoadForProfileAsync(profile))?.GitHubUsername),
            _                      => new AuthSnapshot(StatusAuthState.None),
        };
    }

    internal static string FormatAuthLine(AuthSnapshot auth, DateTimeOffset now) => auth.State switch {
        StatusAuthState.NotRequired => "not required — this server asks for no auth",
        StatusAuthState.Machine or StatusAuthState.MachineIncomplete
                                    => auth.MachineLine!,
        StatusAuthState.Valid       => $"{auth.Identity} ✓ token valid ({FormatExpiry(auth.ExpiresAt!.Value - now)})",
        StatusAuthState.Expired     => $"{auth.Identity} ✗ token expired (run: kcap login)",
        StatusAuthState.WrongServer => $"✗ token was issued by {auth.IssuedServerUrl ?? "another server"} (run: kcap login)",
        StatusAuthState.None        => "not authenticated (run: kcap login)",
    };

    /// <summary>The version facts behind the Version line, without printing it.</summary>
    async Task<(string Current, UpdateAdvisory Advisory, bool Bundled)> ResolveVersionAsync(string[] args) {
        var current = CapacitorVersion.CurrentDisplay();

        if (_appBundled) return (current, default, true);

        var profile = profiles.Effective;

        if (args.Contains("--no-update-check") || profile?.UpdateCheck == false) return (current, default, false);

        var channel = UpdateCommand.ResolveChannel(args, profile?.UpdateChannel);
        var result  = await UpdateNotice.GetSharedCheckAsync(channel, config, npm, time);

        return (current, UpdateAdvisoryResolver.Resolve(result, channel, profiles.Resolution.ServerUrl, config), false);
    }

    internal static string FormatExpiry(TimeSpan remaining) =>
        remaining.TotalHours > 1
            ? $"expires in {remaining.TotalHours:F0}h"
            : $"expires in {remaining.TotalMinutes:F0}m";

    static string InstallCommandFor(HarnessId id) =>
        id.PluginInstallFlag is { } flag ? $"kcap plugin install {flag}" : "kcap plugin install";

    /// <param name="StatusCode">Set only when the server answered with a failure status.</param>
    /// <param name="Provider">The auth provider the server announces, or null when that is not
    /// known. Never a guess: an unanswered probe leaves it to the last answer on disk.</param>
    internal sealed record ServerReach(string? Url, bool Reachable, int? StatusCode, string? Provider = null);

    internal sealed record AuthSnapshot(
        StatusAuthState State, string? Identity = null, DateTimeOffset? ExpiresAt = null, string? MachineLine = null,
        string? IssuedServerUrl = null);

    async Task WriteVersionLineAsync(string[] args) {
        Console.Write("  Version: ");

        var current = CapacitorVersion.CurrentDisplay();

        if (_appBundled) {
            await Console.Out.WriteLineAsync(FormatBundledVersionLine(current));

            return;
        }

        // Opt-out: an explicit --no-update-check flag or a disabled profile setting means no
        // check is performed at all (never force one the user turned off) — the line still
        // prints the bare version.
        if (args.Contains("--no-update-check")) {
            await Console.Out.WriteLineAsync(FormatVersionLine(current, default));

            return;
        }

        var profile = profiles.Effective;

        if (profile?.UpdateCheck == false) {
            await Console.Out.WriteLineAsync(FormatVersionLine(current, default));

            return;
        }

        var channel  = UpdateCommand.ResolveChannel(args, profile?.UpdateChannel);
        var result   = await UpdateNotice.GetSharedCheckAsync(channel, config, npm, time);

        // Cap the recommendation at the connected server's version (min(npm latest, server)).
        var advisory = UpdateAdvisoryResolver.Resolve(result, channel, profiles.Resolution.ServerUrl, config);

        await Console.Out.WriteLineAsync(FormatVersionLine(current, advisory));

        if (advisory.Newer) {
            // Surfaced inline already — the exit-time footer (UpdateNotice.FlushAsync) must not
            // print the same information a second time.
            UpdateNotice.MarkReported();
        }
    }

    /// <summary>
    /// Pure formatting for the Version line: <c>kcap {current}</c>, with an inline
    /// <c>(update available: {target})</c> annotation appended only when <paramref name="advisory"/>
    /// reports a newer version — and, when the target was capped at the server's version, a
    /// <c>, server version</c> marker. Split out from <see cref="WriteVersionLineAsync"/> so the exact
    /// text is unit-testable without any I/O.
    /// </summary>
    internal static string FormatVersionLine(string current, UpdateAdvisory advisory) =>
        advisory is { Newer: true, Target: { } target }
            ? advisory.ServerCapped
                ? $"kcap {current} (update available: {target}, server version)"
                : $"kcap {current} (update available: {target})"
            : $"kcap {current}";

    internal static string FormatBundledVersionLine(string current) => $"kcap {current} (bundled with Kurrent Capacitor)";

    static async Task WriteAgentStatusAsync(DaemonStore store) {
        var entries = ReadDaemonEntries(store);
        var live    = entries.Where(e => e.Alive).ToList();

        switch (live.Count) {
            case 0:
                await Console.Out.WriteLineAsync(
                    entries.Count == 0
                        ? "not running"
                        : "not running (stale PID files; `kcap daemon doctor --clean` to remove)"
                );

                return;
            case 1:
                await Console.Out.WriteLineAsync($"running — {live[0].Name} (PID {live[0].Pid})");

                return;
            default: {
                var summary = string.Join(", ", live.Select(e => $"{e.Name} (PID {e.Pid})"));
                await Console.Out.WriteLineAsync($"running ({live.Count}) — {summary}");

                break;
            }
        }
    }

    /// <summary>
    /// Every daemon PID file with the name, pid, and whether that process is still there. Shared by
    /// the Daemon line and the JSON payload so they cannot disagree about what is running.
    /// </summary>
    static List<DaemonEntry> ReadDaemonEntries(DaemonStore store) {
        var entries = new List<DaemonEntry>();

        if (!Directory.Exists(store.Directory)) return entries;

        List<string> pidFiles;

        // These files are live state: `daemon stop` and `doctor --clean` remove them, so the
        // directory can change under the sweep. A status read must not die of that.
        try {
            pidFiles = [.. Directory.EnumerateFiles(store.Directory, "*.pid").OrderBy(f => f)];
        } catch (IOException) {
            return entries;
        } catch (UnauthorizedAccessException) {
            return entries;
        }

        foreach (var pidFile in pidFiles) {
            var name = Path.GetFileNameWithoutExtension(pidFile);

            if (string.IsNullOrEmpty(name)) continue;

            DaemonPidProbe.PidEntry? entry;

            try {
                entry = DaemonPidProbe.ReadPidFile(store, name);
            } catch (IOException) {
                continue;
            } catch (UnauthorizedAccessException) {
                continue;
            }

            // A present but unparseable marker is a hard-death breadcrumb, not an absence: it is
            // counted as stale rather than skipped, so the file someone has to clean up is reported.
            entries.Add(entry is { } e
                ? new DaemonEntry(name, e.Pid, DaemonPidProbe.IsOurDaemon(e.Pid, e.StartToken))
                : new DaemonEntry(name, null, false));
        }

        return entries;
    }

    /// <param name="Pid">Null when the marker is present but carries no usable PID.</param>
    internal sealed record DaemonEntry(string Name, int? Pid, bool Alive);

    /// <summary>
    /// Renders the Hooks status line: every harness, wired or not, in registry order. What "wired"
    /// means is each vendor's own — Gemini merges its hooks into the shared
    /// <c>~/.gemini/settings.json</c>, while Pi and OpenCode track a live-ingest extension file
    /// rather than hooks — but all share the line for at-a-glance parity. Pure: the probing happens
    /// in the caller.
    /// </summary>
    internal static string BuildHooksStatusLine(IEnumerable<(HarnessId Id, bool Wired)> wiring) =>
        string.Join("  ", wiring.Select(w => $"{ShortLabel(w.Id)} {(w.Wired ? "✓" : "✗")}"));

    /// <summary>Every vendor shares one line, so the one label carrying a product suffix is
    /// shortened to fit beside the rest.</summary>
    static string ShortLabel(HarnessId id) => id is HarnessId.Claude ? "Claude" : HarnessRegistry.LabelOf(id);
}

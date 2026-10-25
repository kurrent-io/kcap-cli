using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Local;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// One row of the daemon's agent table (`id\tstatus\trepo\tkind\tflowRunId\tflowRole\ttitle\tstate`
/// on the wire). An older daemon sends only the first three, six or seven columns; the rest default,
/// and a null title or state means the daemon sent no such column.
internal readonly record struct AgentRow(
    string Id, string Status, string Repo, string Kind, string FlowRunId, string FlowRole, string? Title = null,
    string? State = null) {
    /// Ledger rows of agents the daemon is not running: nothing to stop until they resume.
    public bool HasProcess => Status is not ("Interrupted" or "Resuming");
}

/// <summary>
/// `kcap agent start|ls|stop|attach|resume` — drive daemon-hosted agents from the local
/// terminal over the daemon's local control socket.
/// </summary>
internal sealed class AgentCommand(
        DaemonStore store, AccountStore accounts, ConfigRoot config, ProfileContext profiles, UserHome home,
        HarnessRegistry harnesses, BinaryProbe binaries, WorkingDirectory workdir, TimeProvider time, StandardStreams console) {
    const string AccountsCapability = "accounts/1";

    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    internal static readonly string[] KnownSubcommands = ["start", "ls", "stop", "attach", "resume"];

    /// Verbs that only ever belonged to the pre-rename `agent` daemon group, minus the
    /// start/stop the new group also owns.
    internal static readonly string[] DaemonOnlySubcommands = ["restart", "status", "logs", "doctor", "service"];

    /// Global flags Program.cs reads off the raw argv and leaves in place. They are not this
    /// group's to interpret, and the subcommand parsers reject unknown flags, so drop them first.
    static readonly string[] GlobalFlags = ["--no-update-check"];

    /// <summary>
    /// Bare `kcap agent` lists agents, and so does a leading flag — `kcap agent --daemon dev`
    /// is an `ls` with an option, not a subcommand named `--daemon`.
    /// </summary>
    internal static (string Sub, string[] Args) SplitSubcommand(string[] args) =>
        args.Length > 1 && !args[1].StartsWith('-') ? (args[1], args[2..]) : ("ls", args[1..]);

    public async Task<int> HandleAsync(string[] args, string? baseUrl) {
        var (sub, rest) = SplitSubcommand([.. args.Where(a => !GlobalFlags.Contains(a))]);

        // `agent` was the daemon verb before it was renamed to `daemon`. The two groups still
        // share start/stop, so those dispatch below; the rest only ever meant the daemon, and
        // answering them with a bare usage line is how a healthy daemon reads as dead
        // mid-diagnosis.
        if (DaemonOnlySubcommands.Contains(sub)) {
            await console.Error.WriteLineAsync($"kcap agent: unknown subcommand '{sub}'");
            await console.Error.WriteLineAsync($"`{sub}` manages the daemon — run `kcap daemon {sub}`.");

            return 1;
        }

        switch (sub) {
            case "start":  return await RunAsync(rest, baseUrl);
            case "ls":     return await ListAsync(rest);
            case "stop":   return await StopAsync(rest);
            case "attach": return await AttachAsync(rest);
            case "resume": return await ResumeAsync(rest);
            default:
                await console.Error.WriteLineAsync($"kcap agent: unknown subcommand '{sub}'");
                await console.Error.WriteLineAsync($"Usage: kcap agent <{string.Join('|', KnownSubcommands)}>");
                await console.Error.WriteLineAsync("Run `kcap agent --help` for details.");

                return 1;
        }
    }

    async Task<int> RunAsync(string[] args, string? baseUrl) {
        // ls/stop/attach only ever talk to the local socket, so the group is offline-callable.
        // Starting an agent is the one subcommand that needs a server for the daemon to record to.
        if (baseUrl is null) {
            await console.Error.WriteLineAsync("kcap agent start: no server configured. Run `kcap setup` or set KCAP_URL.");

            return 1;
        }

        var parsed = AgentStartArgs.Parse(args);
        if (parsed.Error is not null) {
            await console.Error.WriteLineAsync($"kcap agent start: {parsed.Error}");

            return 1;
        }

        ResolvedAccount? account = null;
        if (parsed.Account is not null) {
            account = await ResolveAccountAsync(parsed);
            if (account is null) return 1;
        }

        var name = ResolveName(parsed.DaemonName);
        if (!await EnsureDaemonAsync(name)) return 1;

        // A daemon without accounts/1 never reads the account id and would start the agent under its default account.
        if (account is not null && !await DaemonChoosesAccountsAsync(name)) {
            await console.Error.WriteLineAsync("kcap agent start: this daemon does not support choosing an account; update kcap on this machine");

            return 1;
        }

        var sock = store.SocketPath(name);
        var work = parsed.Worktree ? WorkLocation.OwnedWorktree : WorkLocation.BorrowedCwd;
        var (cols, rows) = TermSize();
        var spawn = FrameCodec.Spawn(
            parsed.Vendor, work, parsed.Private, workdir.Path, parsed.Passthrough, cols, rows, parsed.StartTitle, account?.AccountId);

        return parsed.Detached
            ? await SpawnDetachedAsync(sock, spawn)
            : await LocalAgentClient.RunAsync(sock, spawn, time, CancellationToken.None);
    }

    /// <summary>Null after printing why the <c>--account</c> value cannot be used.</summary>
    async Task<ResolvedAccount?> ResolveAccountAsync(AgentStartArgs parsed) {
        AccountRegistry registry;
        try {
            registry = accounts.Load();
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            await console.Error.WriteLineAsync($"kcap agent start: could not read the kcap account registry in {accounts.Directory}: {ex.Message}");

            return null;
        }

        if (HarnessNames.From(parsed.Vendor) is not ({ } vendor and (HarnessId.Claude or HarnessId.Codex))) {
            await console.Error.WriteLineAsync($"kcap agent start: cannot use --account with {parsed.Vendor}: accounts apply to claude and codex only");

            return null;
        }

        var outcome = AccountResolution.ResolveArgument(registry, vendor, AccountDirectory.ExpandHome(parsed.Account!, home));
        if (outcome.Account is not null) return outcome.Account;

        await console.Error.WriteLineAsync($"kcap agent start: {outcome.Refusal}");

        return null;
    }

    async Task<bool> DaemonChoosesAccountsAsync(string name) =>
        (await HelloProbe.RunAsync(store, name, time, HelloTimeout)).Capabilities.Contains(AccountsCapability);

    async Task<int> AttachAsync(string[] args) {
        if (args.Length == 0 || args[0].StartsWith('-')) {
            await console.Error.WriteLineAsync("usage: kcap agent attach <agent-id> [--daemon <name>]");

            return 1;
        }

        var (daemonName, daemonError) = DaemonNameFrom(args);
        if (daemonError is not null) {
            await console.Error.WriteLineAsync($"kcap agent: {daemonError}");

            return 1;
        }

        var sock = store.SocketPath(ResolveName(daemonName));

        if (!File.Exists(sock)) {
            await console.Error.WriteLineAsync($"kcap: no daemon socket at {sock}");

            return 1;
        }

        var agentId = await ResolveOrReportAsync(sock, args[0]);
        if (agentId is null) return 1;

        var reconnect = new AttachReconnect(
            agentId, AttachReconnectWindow.Resolve(), () => ResumeLedgerStore.FindByAgentId(store, agentId));

        return await LocalAgentClient.RunAsync(
            sock, new LocalFrame(FrameType.Attach) { Text = agentId }, time, ConsoleAgentTerminal.Instance, reconnect,
            CancellationToken.None);
    }

    async Task<int> ResumeAsync(string[] args) {
        if (args.Length == 0 || args[0].StartsWith('-')) {
            await console.Error.WriteLineAsync("usage: kcap agent resume <agent-id> [--daemon <name>]");

            return 1;
        }

        var (daemonName, daemonError) = DaemonNameFrom(args);
        if (daemonError is not null) {
            await console.Error.WriteLineAsync($"kcap agent: {daemonError}");

            return 1;
        }

        var sock = store.SocketPath(ResolveName(daemonName));

        if (!File.Exists(sock)) {
            await console.Error.WriteLineAsync($"kcap: no daemon socket at {sock}");

            return 1;
        }

        var agentId = await ResolveOrReportAsync(sock, args[0]);
        if (agentId is null) return 1;

        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, LocalFrame.AgentResume(agentId), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            switch (resp) {
                case { Type: FrameType.AgentResumeAck, Text: "resuming" }:
                    console.Out.WriteLine($"Resuming {agentId}.");

                    return 0;
                case { Type: FrameType.AgentResumeAck, Text: "not_interrupted" }:
                    await console.Error.WriteLineAsync($"{agentId} is not interrupted.");

                    return 1;
                case { Type: FrameType.AgentResumeAck, Text: "unknown" }:
                    await console.Error.WriteLineAsync($"No agent {agentId}.");

                    return 1;
                case { Type: FrameType.AgentResumeAck, Text: { } blocked } when blocked.StartsWith("blocked:", StringComparison.Ordinal):
                    await console.Error.WriteLineAsync($"{agentId} cannot be resumed: {blocked["blocked:".Length..]}.");

                    return 1;
                case { Type: FrameType.Error }:
                    await console.Error.WriteLineAsync($"kcap: {resp.Text}");

                    return 1;
                case null:
                    await console.Error.WriteLineAsync("kcap: this daemon is too old for `agent resume` — restart it with `kcap daemon restart`");

                    return 1;
                default:
                    await console.Error.WriteLineAsync($"kcap: unexpected daemon response to resume ({resp.Type} {resp.Text})");

                    return 1;
            }
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return 1;
        }
    }

    async Task<int> StopAsync(string[] args) {
        var all = args.Contains("--all");
        var yes = args.Contains("--yes") || args.Contains("-y");
        var force = args.Contains("--force");
        var hasId = args.Length > 0 && !args[0].StartsWith('-');

        if (all && hasId) {
            await console.Error.WriteLineAsync("kcap agent stop: cannot combine an agent id with --all");

            return 1;
        }

        if (!all && !hasId) {
            await console.Error.WriteLineAsync("usage: kcap agent stop <agent-id> [--force] [--daemon <name>]");
            await console.Error.WriteLineAsync("       kcap agent stop --all [-y] [--force] [--daemon <name>]");

            return 1;
        }

        var (daemonName, daemonError) = DaemonNameFrom(args);
        if (daemonError is not null) {
            await console.Error.WriteLineAsync($"kcap agent: {daemonError}");

            return 1;
        }

        var name = ResolveName(daemonName);
        var sock = store.SocketPath(name);

        if (!File.Exists(sock)) {
            await console.Error.WriteLineAsync($"kcap: no daemon socket at {sock}");

            return 1;
        }

        string[] targets;

        if (all) {
            var agents = await FetchAgentsAsync(sock);
            if (agents is null) return 1;

            if (agents.Count == 0) {
                console.Out.WriteLine("No agents.");

                return 0;
            }

            agents = [.. agents.Where(a => a.HasProcess)];
            if (agents.Count == 0) {
                console.Out.WriteLine("No agents.");

                return 0;
            }

            var (stoppable, protectedIds) = PartitionByProtection(agents);
            targets = force ? agents.Select(a => a.Id).ToArray() : stoppable;

            if (targets.Length == 0) {
                console.Out.WriteLine(protectedIds.Length > 0
                    ? $"No agents to stop. {protectedIds.Length} review agent(s) skipped — pass --force to include them."
                    : "No agents.");

                return 0;
            }

            console.Out.WriteLine($"Found {targets.Length} agents:");
            foreach (var a in agents.Where(a => targets.Contains(a.Id) && !protectedIds.Contains(a.Id)))
                console.Out.WriteLine($"  • {a.Id}  {a.Repo}");

            // Both branches label protected rows with their Kind before the [y/N] prompt, so the
            // blast radius of --force is as visible as the default skip list is.
            if (protectedIds.Length > 0) {
                console.Out.WriteLine(force
                    ? $"Including {protectedIds.Length} review agent(s):"
                    : $"Skipping {protectedIds.Length} review agent(s) — pass --force to include them:");
                foreach (var a in agents.Where(a => protectedIds.Contains(a.Id)))
                    console.Out.WriteLine($"  • {a.Id}  {a.Kind}  {a.Repo}");
            }

            if (!yes) {
                await console.Out.WriteAsync($"Stop {targets.Length}? [y/N] ");
                var reply = await console.In.ReadLineAsync();

                if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                    await console.Out.WriteLineAsync("Cancelled.");

                    return 0;
                }
            }
        } else {
            var resolved = await ResolveOrReportAsync(sock, args[0]);
            if (resolved is null) return 1;

            targets = [resolved];
        }

        // Each confirmed id is sent on its own: an empty StopV2 id would expand to the daemon's
        // current set, including agents started after the confirmation.
        var results = await Task.WhenAll(targets.Select(id => SendStopAsync(sock, id, name, force, missingIsStopped: all)));

        return results.Any(code => code != 0) ? 1 : 0;
    }

    /// <summary>One outcome per line of a StopAck, in the daemon's order. A line with no status, or
    /// one the client does not know, is a failure; "missing" counts only for the agent this request
    /// named.</summary>
    internal static IReadOnlyList<(string Id, StopAckOutcome Outcome)> ReadStopAck(string text, string agentId, bool missingIsStopped) {
        if (text.Length == 0) return [];

        return [.. text.Split('\n').Select(line => {
            var parts = line.Split('\t');
            var outcome = (parts.Length > 1 ? parts[1] : null) switch {
                "stopped" => StopAckOutcome.Stopped,
                "skipped" => StopAckOutcome.Skipped,
                "missing" when parts[0] == agentId && parts.Length == 2 =>
                    missingIsStopped ? StopAckOutcome.AlreadyStopped : StopAckOutcome.NoSuchAgent,
                _ => StopAckOutcome.Failed,
            };
            return (parts[0], outcome);
        })];
    }

    async Task<int> SendStopAsync(string sock, string agentId, string daemonName, bool force, bool missingIsStopped) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, LocalFrame.StopV2(force, agentId), default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            switch (resp?.Type) {
                case FrameType.StopAck:
                    var outcomes = ReadStopAck(resp.Text, agentId, missingIsStopped);
                    if (outcomes.Count == 0) { console.Out.WriteLine("No agents."); return 0; }

                    foreach (var (id, outcome) in outcomes) {
                        switch (outcome) {
                            case StopAckOutcome.Stopped:        console.Out.WriteLine($"Stopped {id}."); break;
                            case StopAckOutcome.Skipped:        console.Out.WriteLine($"Skipped {id} — review agent; pass --force to stop it."); break;
                            case StopAckOutcome.AlreadyStopped: console.Out.WriteLine($"Already stopped {id}."); break;
                            case StopAckOutcome.NoSuchAgent:    console.Error.WriteLine($"kcap: no such agent {id}"); break;
                            default:                            console.Error.WriteLine($"Failed to stop {id} — see `kcap daemon logs`."); break;
                        }
                    }

                    var skipped = outcomes.Count(o => o.Outcome == StopAckOutcome.Skipped);
                    if (skipped > 0)
                        console.Out.WriteLine($"{skipped} review agent(s) left running — pass --force to stop them.");

                    return outcomes.Any(o => o.Outcome.IsFailure()) ? 1 : 0;
                case FrameType.Error:
                    await console.Error.WriteLineAsync($"kcap: {resp.Text}");

                    return 1;
                case null:
                    // An older daemon can't decode frame type 10 (StopV2): it faults before its
                    // frame switch and closes without replying, so we see a clean EOF. `--force`
                    // here is the restart flag, not the stop flag — this daemon is guaranteed
                    // busy since the stop it never understood never ran, so `restart` needs its
                    // own override to proceed.
                    await console.Error.WriteLineAsync(
                        $"kcap: this daemon is too old for `agent stop` — restart it with `kcap daemon restart --force --name {daemonName}`");

                    return 1;
                default:
                    await console.Error.WriteLineAsync($"kcap: unexpected daemon response to stop ({resp.Type})");

                    return 1;
            }
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return 1;
        }
    }

    /// <summary>Resolves an id or prefix, printing the reason and returning null on failure.</summary>
    async Task<string?> ResolveOrReportAsync(string sock, string given) {
        if (IsFullAgentId(given)) return given.ToLowerInvariant(); // skip the round-trip; ResolveAgentId agrees

        var agents = await FetchAgentsAsync(sock);
        if (agents is null) return null;

        var (id, error) = ResolveAgentId(agents, given);
        if (error is not null) await console.Error.WriteLineAsync($"kcap: {error}");

        return id;
    }

    async Task<int> ListAsync(string[] args) {
        var (daemonName, daemonError) = DaemonNameFrom(args);
        if (daemonError is not null) {
            await console.Error.WriteLineAsync($"kcap agent: {daemonError}");

            return 1;
        }

        var sock = store.SocketPath(ResolveName(daemonName));

        if (!File.Exists(sock)) {
            console.Out.WriteLine("No local daemon running.");

            return 0;
        }

        var agents = await FetchAgentsAsync(sock);
        if (agents is null) return 1;

        if (agents.Count == 0) {
            console.Out.WriteLine("No agents.");

            return 0;
        }

        foreach (var line in FormatAgentTable(agents)) console.Out.WriteLine(line);

        return 0;
    }

    /// The title goes last: it is the widest free-form column, so the repo column is padded to
    /// its longest value instead. Free-form cells come from launch prompts, repo paths and resume
    /// failures, so control characters are stripped before they reach the terminal.
    internal static IEnumerable<string> FormatAgentTable(IReadOnlyList<AgentRow> agents) {
        static string RepoCell(AgentRow a) {
            var flow = a.FlowRunId.Length > 0 ? $"  [flow {a.FlowRunId}]" : "";
            var role = a.FlowRole.Length > 0 ? $"  [{a.FlowRole}]" : "";

            return HttpClientExtensions.StripControlCharacters($"{a.Repo}{flow}{role}");
        }

        static string StateCell(AgentRow a) => HttpClientExtensions.StripControlCharacters(a.State ?? "");

        var withState   = agents.Any(a => a.State is not null);
        var statusWidth = Math.Max(10, agents.Max(a => a.Status.Length));
        var stateWidth  = withState ? Math.Max("STATE".Length, agents.Max(a => StateCell(a).Length)) : 0;

        string Lead(string id, string status, string state, string kind) =>
            withState
                ? $"{id,-34} {status.PadRight(statusWidth)} {state.PadRight(stateWidth)} {kind,-12}"
                : $"{id,-34} {status.PadRight(statusWidth)} {kind,-12}";

        if (agents.All(a => a.Title is null)) {
            yield return $"{Lead("AGENT", "STATUS", "STATE", "KIND")} REPO";
            foreach (var a in agents) yield return $"{Lead(a.Id, a.Status, StateCell(a), a.Kind)} {RepoCell(a)}";

            yield break;
        }

        var repoWidth = Math.Max("REPO".Length, agents.Max(a => RepoCell(a).Length));

        yield return $"{Lead("AGENT", "STATUS", "STATE", "KIND")} {"REPO".PadRight(repoWidth)} TITLE";
        foreach (var a in agents)
            yield return $"{Lead(a.Id, a.Status, StateCell(a), a.Kind)} {RepoCell(a).PadRight(repoWidth)} {HttpClientExtensions.StripControlCharacters(a.Title)}".TrimEnd();
    }

    /// <summary>
    /// Asks the daemon for its agent table. Returns null — after reporting the reason — for any
    /// non-answer: a closed connection, an Error frame, or an unexpected frame type. Only a real
    /// AgentList frame with an empty payload is a genuinely empty table, so only that maps to [].
    /// </summary>
    async Task<IReadOnlyList<AgentRow>?> FetchAgentsAsync(string sock) {
        var rows = await FetchAgentTableAsync(sock, LocalFrame.ListWithState());

        // A current daemon never answers the state request with six columns; one that predates it
        // does, and still gives titles to the older request.
        if (rows is { Count: > 0 } && rows.All(r => r.Columns == 6))
            rows = await FetchAgentTableAsync(sock, LocalFrame.ListWithTitles());

        return rows?.Select(r => r.Row).ToList();
    }

    async Task<IReadOnlyList<(AgentRow Row, int Columns)>?> FetchAgentTableAsync(string sock, LocalFrame request) {
        try {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await FrameCodec.WriteAsync(stream, request, default);
            var resp = await FrameCodec.ReadAsync(stream, default);

            if (resp is null) {
                await console.Error.WriteLineAsync("kcap: daemon closed the connection without replying to list");

                return null;
            }

            if (resp.Type == FrameType.Error) {
                await console.Error.WriteLineAsync($"kcap: {resp.Text}");

                return null;
            }

            if (resp.Type != FrameType.AgentList) {
                await console.Error.WriteLineAsync($"kcap: unexpected daemon response to list ({resp.Type})");

                return null;
            }

            if (ParseAgentTable(resp.Text) is { } table) return table;

            await console.Error.WriteLineAsync(
                "kcap: daemon sent a malformed agent table (unexpected column count or empty id); refusing to act on it");

            return null;
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await console.Error.WriteLineAsync($"kcap: cannot reach daemon: {ex.Message}");

            return null;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    async Task<int> SpawnDetachedAsync(string sock, LocalFrame spawn) {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(sock));
        await using var stream = new NetworkStream(socket, ownsSocket: false);

        await FrameCodec.WriteAsync(stream, spawn, default);
        var f = await FrameCodec.ReadAsync(stream, default);

        switch (f?.Type) {
            case FrameType.Attached:
                var (id, _) = FrameCodec.Attached(f);
                console.Out.WriteLine($"Started agent {id} (detached). Attach with: kcap agent attach {id}");

                return 0;
            case FrameType.Error:
                await console.Error.WriteLineAsync($"kcap: {f.Text}");

                return 1;
            default:
                await console.Error.WriteLineAsync("kcap: unexpected daemon response to spawn");

                return 1;
        }
    }

    /// <summary>Connects if a daemon is up; otherwise starts one detached and waits for the socket.</summary>
    async Task<bool> EnsureDaemonAsync(string name) {
        var sock = store.SocketPath(name);
        if (await CanConnectAsync(sock)) return true;

        await console.Error.WriteLineAsync($"kcap: starting daemon '{name}'…");
        await new DaemonCommands(store, config, profiles, home, harnesses, binaries, time, console)
            .HandleAsync(["daemon", "start", "-d", "--name", name]);

        var deadline = time.GetUtcNow().UtcDateTime.AddSeconds(15);
        while (time.GetUtcNow().UtcDateTime < deadline) {
            if (await CanConnectAsync(sock)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(250), time);
        }

        await console.Error.WriteLineAsync("kcap: daemon did not come up in time (check `kcap daemon logs`).");

        return false;
    }

    static async Task<bool> CanConnectAsync(string sock) {
        if (!File.Exists(sock)) return false;

        try {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await s.ConnectAsync(new UnixDomainSocketEndPoint(sock));

            return true;
        } catch {
            return false;
        }
    }

    string ResolveName(string? daemonName) {
        string[] args = daemonName is null ? [] : ["--name", daemonName];

        return DaemonNameResolver.Resolve(args, profiles.DaemonName);
    }

    /// <summary>
    /// Parses `--daemon &lt;name&gt;` out of the shared `ls`/`attach`/`stop`/`resume` arg list. Absent
    /// entirely resolves to (null, null) — meaning "use the default daemon", the correct existing
    /// behaviour. Present with no value, or with a following flag instead of a value, is an error
    /// rather than silently falling back to the default: a typo here must not retarget a
    /// destructive `stop` at the wrong (and possibly unconfirmed, with `-y`) daemon.
    /// </summary>
    internal static (string? Name, string? Error) DaemonNameFrom(string[] args) {
        var i = Array.IndexOf(args, "--daemon");
        if (i < 0) return (null, null);

        var next = i + 1 < args.Length ? args[i + 1] : null;

        return string.IsNullOrEmpty(next) || next.StartsWith('-')
            ? (null, "--daemon requires a value")
            : (next, null);
    }

    /// Kinds the CLI refuses to mutate by accident: a reviewer mid-round is not the user's to
    /// type at or stop. Mirrors LaunchKind — anything that is not a plain agent is protected,
    /// including a kind this build doesn't recognise: an unrecognised kind fails safe rather
    /// than reading as stoppable.
    internal static bool IsProtectedKind(string kind) => kind is not "agent";

    /// <summary>Splits an agent list into what `stop --all` will stop and what it will skip.</summary>
    internal static (string[] Stoppable, string[] Protected) PartitionByProtection(IReadOnlyList<AgentRow> agents) => (
        [.. agents.Where(a => !IsProtectedKind(a.Kind)).Select(a => a.Id)],
        [.. agents.Where(a => IsProtectedKind(a.Kind)).Select(a => a.Id)]
    );

    /// <summary>Tolerates a short row from an older daemon by defaulting the newer columns.</summary>
    /// <summary>Null for a table with any row the client cannot read safely. Older daemons send 3, 6
    /// or 7 columns; current ones send 8. Shifted columns can hide a protected kind, and an empty id
    /// would turn a targeted stop into stop-all.</summary>
    internal static IReadOnlyList<(AgentRow Row, int Columns)>? ParseAgentTable(string text) {
        string[] rows = [.. text.Split('\n').Where(l => l.Length > 0)];

        if (rows.Any(l => {
                var cells = l.Split('\t');

                return cells.Length is not (3 or 6 or 7 or 8) || string.IsNullOrWhiteSpace(cells[0]);
            }))
            return null;

        return [.. rows.Select(l => (ParseAgentRow(l), l.Split('\t').Length))];
    }

    internal static AgentRow ParseAgentRow(string line) {
        var p = line.Split('\t');

        return new AgentRow(
            p[0],
            p.Length > 1 ? p[1] : "",
            p.Length > 2 ? p[2] : "",
            p.Length > 3 && p[3].Length > 0 ? p[3] : "agent",
            p.Length > 4 ? p[4] : "",
            p.Length > 5 ? p[5] : "",
            p.Length > 6 ? p[6] : null,
            p.Length > 7 ? p[7] : null);
    }

    /// <summary>A full agent id as minted by `Guid.NewGuid().ToString("N")`.</summary>
    internal static bool IsFullAgentId(string s) => s.Length == 32 && s.All(char.IsAsciiHexDigit);

    /// <summary>
    /// A full 32-hex id is used verbatim — it may name an agent that survived a previous
    /// daemon incarnation and so is absent from the live list, which the daemon can still
    /// reap by PID record. Anything shorter is a prefix and must match exactly one agent.
    /// </summary>
    internal static (string? Id, string? Error) ResolveAgentId(IReadOnlyList<AgentRow> agents, string given) {
        // Lowercase: the daemon's lookups (_agents.TryGetValue, TryStopByPidRecordAsync) are
        // ordinal, so an uppercase full id must be normalized here to match the lowercase ids
        // the daemon mints — the prefix path below is already case-insensitive.
        if (IsFullAgentId(given)) return (given.ToLowerInvariant(), null);

        var hits = agents.Where(a => a.Id.StartsWith(given, StringComparison.OrdinalIgnoreCase)).ToList();

        return hits.Count switch {
            1 => (hits[0].Id, null),
            0 => (null, $"no agent matching '{given}'"),
            _ => (null, $"'{given}' matches {hits.Count} agents:{string.Concat(hits.Select(h => $"\n  {h.Id}  {h.Repo}"))}"),
        };
    }

    static (ushort Cols, ushort Rows) TermSize() {
        try { return ((ushort)Math.Max(1, Console.WindowWidth), (ushort)Math.Max(1, Console.WindowHeight)); }
        catch { return (120, 40); }
    }
}

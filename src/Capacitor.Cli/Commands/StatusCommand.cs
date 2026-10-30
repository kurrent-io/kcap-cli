using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

public sealed class StatusCommand(
        DaemonStore store, ProfileContext profiles, ConfigRoot config, TokenStore tokenStore, HarnessRegistry harnesses,
        ICapacitorHttpClient http, IReleaseFeed npm, MachineAuth machine, TimeProvider time,
        AccountStore accounts, UserHome home, bool? appBundled = null) {

    readonly bool _appBundled = appBundled ?? InstallProvenance.IsAppBundled();

    public async Task<int> HandleAsync(string[] args) {
        var baseUrl = profiles.Resolution.ServerUrl;

        if (args.Contains("--json")) return await WriteJsonAsync(args, baseUrl);

        var glyphs = StatusGlyphs.Encodable(Console.OutputEncoding);

        Task Line(string text) => Console.Out.WriteLineAsync(glyphs ? text : StatusGlyphs.Plain(text));

        // Version line reuses UpdateNotice's shared check and marks-reported so the exit footer
        // doesn't double-print; respects the same opt-outs.
        await WriteVersionLineAsync(args);

        // Server. The URL goes out ahead of the probe, which can take its whole timeout: a bare
        // label would say nothing about what is being waited on.
        Console.Write(baseUrl is null ? "  Server:  " : $"  Server:  {baseUrl} ");
        var server = await ProbeServerAsync(http, baseUrl, config, time);
        await Line(FormatReachability(server));

        if (UnreachableRemedy(server) is { } remedy) await Line($"           {remedy}");

        if (profiles.Resolution.IgnoredRepoProfile is { } ignored)
            await Line($"           {IgnoredRepoProfileLine(ignored)}");

        // Auth
        var auth = await ResolveAuthAsync(tokenStore, machine, profiles.Name, server, profiles.Resolution.Endpoint);
        await Line($"  Auth:    {FormatAuthLine(auth, time.GetUtcNow())}");

        // Hooks
        await Console.Out.WriteAsync("  Hooks:   ");

        var codexRefused = CodexRefusedHooks(harnesses);
        var line         = BuildHooksStatusLine(harnesses.Select(h => (h.Id, HooksRun(h.Id, h.Signals.IsWired, codexRefused))));

        await Line(WrapHooksLine(glyphs ? line : StatusGlyphs.Plain(line), TerminalWidth()));

        if (codexRefused is not null) await Line($"           {CodexHooksDetailLine(codexRefused)}");

        var accountLines = BuildAccountLines(
            accounts.TryLoad()?.Accounts.Select(a => (a, AccountWiring.State(a, home))) ?? [],
            vendor => AccountLayouts.DefaultDirectory(vendor, home));
        if (accountLines.Length > 0) await Line(accountLines);

        // Newly-installed-but-unconfigured harnesses. Ledger-independent (a dismissed vendor is
        // still surfaced here) — status always tells the truth, unlike the nudge which respects
        // dismissals. Shares the wired-check with the Hooks line above, so the two never disagree.
        foreach (var h in harnesses) {
            if (!harnesses.Detected(h.Id)) continue;
            if (h.Signals.IsWired) continue;
            await Line($"           {h.Label} installed but kcap not configured — run `{InstallCommandFor(h.Id)}`");
        }

        var activity = new HookActivity(config, time);

        foreach (var h in harnesses.Where(h => HooksRun(h.Id, h.Signals.IsWired, codexRefused)))
            await Line($"           {RecordingLine(h.Id, h.Label, activity.LastEvent(h.VendorId), time.GetUtcNow())}");

        if (harnesses.FirstOrDefault(h => h.Id == HarnessId.Muse) is { } muse
            && MuseHooksDetailLine(harnesses.Of<MuseHarness>().Paths.SettingsJson,
                   detected: harnesses.Detected(HarnessId.Muse), wired: muse.Signals.IsWired) is { } museGap)
            await Line($"           {museGap}");

        if (new GitHookInstaller(home).IsInstalled() && GitHookWarning(GitHookInstaller.InstalledGitVersion()) is { } gitWarning)
            await Line($"  Git:     {gitWarning}");

        // Daemon: the per-name PID files `kcap daemon status` reads, so the two agree.
        var daemons = ReadDaemonEntries(store);
        await Line($"  Daemon:  {DaemonLine(daemons)}");

        var (units, verifySupported) = await InstalledUnitsAsync();
        foreach (var warning in DaemonDriftLines(
                     CapacitorVersion.Current(), daemons, units, UnitIdentity.ResolveDaemonBinary(), verifySupported))
            await Line($"           {warning}");

        return 0;
    }

    /// <summary>
    /// The same facts the human lines carry, as one document on stdout and nothing else. Every value
    /// comes from the gatherers the text path uses, so the two cannot drift.
    /// </summary>
    async Task<int> WriteJsonAsync(string[] args, string? baseUrl) {
        var (current, advisory, bundled) = await ResolveVersionAsync(args);
        var server = await ProbeServerAsync(http, baseUrl, config, time);
        var auth   = await ResolveAuthAsync(tokenStore, machine, profiles.Name, server, profiles.Resolution.Endpoint);

        // The exit-time footer is the human surface for this, and the payload already carries it.
        if (advisory.Newer) UpdateNotice.MarkReported();

        var activity     = new HookActivity(config, time);
        var codexRefused = CodexRefusedHooks(harnesses);

        var payload = BuildPayload(
            profiles.Name, server, auth, current, advisory, bundled,
            [.. harnesses.Select(h => {
                var installed = harnesses.Detected(h.Id);
                var wired     = HooksRun(h.Id, h.Signals.IsWired, codexRefused);

                return new StatusHarnessJson(
                    h.VendorId, installed, wired, installed && !wired ? InstallCommandFor(h.Id) : null,
                    wired ? activity.LastEvent(h.VendorId) : null);
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
                [.. live.Select(e => new StatusDaemonEntryJson(e.Name, e.Pid!.Value, e.Version is { } v ? CapacitorVersion.Display(v) : null))],
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
        } catch (Exception ex) {
            return new ServerReach(baseUrl, false, null, LastKnownProvider(), ConnectionFailures.Of(ex));
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
        : $"✗ unreachable ({(server.Failure ?? ConnectionFailure.Other).Reason})";

    /// <summary>What to do about a server that gave no answer, or null when it answered.</summary>
    internal static string? UnreachableRemedy(ServerReach server) =>
        server.Unanswered ? (server.Failure ?? ConnectionFailure.Other).Remedy : null;

    /// <summary>
    /// Decided in the order the credential lane picks a source — no auth, then the machine, then the
    /// token store — and each one REPLACES the ones after it rather than adding to them. With
    /// KCAP_CLIENT_ID/KCAP_CLIENT_SECRET in the environment, MachineAuth.Intended bypasses the token
    /// store entirely, so its state is not what this CLI authenticates with. Reporting both would
    /// show a headless runner as recording as the machine AND not authenticated, contradictory and
    /// with irrelevant remediation.
    /// </summary>
    internal static async Task<AuthSnapshot> ResolveAuthAsync(
            TokenStore tokens, MachineAuth machine, string profile, ServerReach server, LocalEndpointClass endpoint) {
        // Ahead of the provider: through an outage that is the last one cached, and a token left in
        // the store must not be refreshed for a server it is never sent to.
        switch (endpoint) {
            case LocalEndpointClass.Local:
                return new AuthSnapshot(StatusAuthState.NotRequired);
            case LocalEndpointClass.Retired:
                return new AuthSnapshot(StatusAuthState.LocalUnavailable,
                    Problem: $"{server.Url} belonged to a removed local installation (run: kcap setup)");
            case LocalEndpointClass.Undetermined(var reason):
                return new AuthSnapshot(StatusAuthState.LocalUnavailable,
                    Problem: $"cannot tell whether {server.Url} is the local installation: {reason}");
        }

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
            AuthStatus.Expired     => new AuthSnapshot(StatusAuthState.Expired, (await tokens.LoadForProfileAsync(profile))?.GitHubUsername,
                                          ServerUnanswered: server.Unanswered),
            _                      => new AuthSnapshot(StatusAuthState.None, ServerUnanswered: server.Unanswered),
        };
    }

    internal static string FormatAuthLine(AuthSnapshot auth, DateTimeOffset now) => auth.State switch {
        StatusAuthState.NotRequired => "not required — this server asks for no auth",
        StatusAuthState.Machine or StatusAuthState.MachineIncomplete
                                    => auth.MachineLine!,
        StatusAuthState.Valid       => $"{auth.Identity} ✓ token valid ({FormatExpiry(auth.ExpiresAt!.Value - now)})",
        // A login cannot reach a server that gives no answer, and that is also what stops a refresh.
        StatusAuthState.Expired when auth.ServerUnanswered
                                    => $"{auth.Identity} ✗ token expired; it cannot be renewed until the server answers",
        StatusAuthState.Expired     => $"{auth.Identity} ✗ token expired (run: kcap login)",
        StatusAuthState.WrongServer => $"✗ token was issued by {auth.IssuedServerUrl ?? "another server"} (run: kcap login)",
        StatusAuthState.None when auth.ServerUnanswered
                                    => "unknown — the server gave no answer, so whether it needs a sign-in cannot be checked",
        StatusAuthState.None        => "not authenticated (run: kcap login)",
        StatusAuthState.LocalUnavailable => $"✗ {auth.Problem}",
    };

    /// <summary>The version facts behind the Version line, without printing it.</summary>
    async Task<(string Current, UpdateAdvisory Advisory, bool Bundled)> ResolveVersionAsync(string[] args) {
        var current = CapacitorVersion.CurrentDisplay();

        if (_appBundled) return (current, default, true);

        var profile = profiles.Effective;

        if (args.Contains("--no-update-check") || profile?.UpdateCheck == false) return (current, default, false);

        var channel = UpdateCommand.ResolveChannel(args, UpdateCommand.ConfiguredChannel(profile?.UpdateChannel));
        var result  = await UpdateNotice.GetSharedCheckAsync(channel, config, npm, time);

        return (current, UpdateAdvisoryResolver.Resolve(result, channel, profiles.Resolution.ServerUrl, profiles.Resolution.Endpoint, config), false);
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
    /// <param name="Failure">Why a configured server gave no answer at all.</param>
    internal sealed record ServerReach(
        string? Url, bool Reachable, int? StatusCode, string? Provider = null, ConnectionFailure? Failure = null) {
        /// <summary>Configured, and no answer of any kind: a status, even a 401, is an answer.</summary>
        public bool Unanswered => Url is not null && !Reachable && StatusCode is null;
    }

    /// <param name="ServerUnanswered">The configured server gave no answer, so neither a missing nor an
    /// expired token can be repaired by a sign-in yet.</param>
    internal sealed record AuthSnapshot(
        StatusAuthState State, string? Identity = null, DateTimeOffset? ExpiresAt = null, string? MachineLine = null,
        string? IssuedServerUrl = null, string? Problem = null, bool ServerUnanswered = false);

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

        var channel  = UpdateCommand.ResolveChannel(args, UpdateCommand.ConfiguredChannel(profile?.UpdateChannel));
        var result   = await UpdateNotice.GetSharedCheckAsync(channel, config, npm, time);

        // Cap the recommendation at the connected server's version (min(npm latest, server)).
        var advisory = UpdateAdvisoryResolver.Resolve(result, channel, profiles.Resolution.ServerUrl, profiles.Resolution.Endpoint, config);

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

    internal static string FormatBundledVersionLine(string current) => $"kcap {current} (bundled with the kcap desktop app)";

    internal static string DaemonLine(IReadOnlyList<DaemonEntry> entries) {
        var live = entries.Where(e => e.Alive).ToList();

        return live.Count switch {
            0 when entries.Count == 0 => "not running",
            0 => "not running (stale PID files; `kcap daemon doctor --clean` to remove)",
            1 => $"running — {Describe(live[0])}",
            _ => $"running ({live.Count}) — {string.Join(", ", live.Select(Describe))}",
        };

        static string Describe(DaemonEntry e) =>
            e.Version is { } v ? $"{e.Name} (PID {e.Pid}, {CapacitorVersion.Display(v)})" : $"{e.Name} (PID {e.Pid})";
    }

    /// <summary>How long status gives the unit read. On Windows it is also the budget schtasks shares
    /// across its calls, and a call still running when it is spent is killed.</summary>
    static readonly TimeSpan UnitsBudget = TimeSpan.FromSeconds(3);

    /// <summary>The installed service units and the daemon binary each names; a unit whose binary cannot
    /// be read is kept with a null binary. launchd and systemd read only unit files here. Empty when the
    /// read does not finish within <see cref="UnitsBudget"/>, so a stalled home directory cannot hang status.</summary>
    async Task<(IReadOnlyList<(string Id, string? Binary)> Units, bool VerifySupported)> InstalledUnitsAsync() {
        IServiceManager manager;
        try {
            manager = ServiceManagerFactory.CurrentPlatform() == ServicePlatform.WindowsScheduledTask
                ? new WindowsScheduledTaskServiceManager(config, schtasks: BoundedSchtasks(time.GetTimestamp()))
                : ServiceManagerFactory.ForCurrentOs(config, home, time);
        } catch (PlatformNotSupportedException) {
            return ([], false);
        }

        var verifySupported = manager is LaunchdServiceManager or WindowsScheduledTaskServiceManager or WindowsServiceManager;
        try {
            return (await Task.Run(() => ReadUnits(manager)).WaitAsync(UnitsBudget, time), verifySupported);
        } catch (TimeoutException) {
            return ([], verifySupported);
        }
    }

    Func<string[], (int ExitCode, string StdOut, string StdErr)> BoundedSchtasks(long started) => args => {
        var left = UnitsBudget - time.GetElapsedTime(started);
        if (left <= TimeSpan.Zero) return (1, "", "kcap status ran out of time to ask schtasks");

        var (code, stdout, stderr, timedOut) = ServiceProcess.RunBounded("schtasks", args, left);
        return timedOut ? (1, "", "schtasks timed out and was terminated") : (code, stdout, stderr);
    };

    internal static IReadOnlyList<(string Id, string? Binary)> ReadUnits(IServiceManager manager) {
        IReadOnlyList<string>? ids;
        try {
            ids = manager.TryListInstalled();
        } catch (Exception ex) when (IsUnitReadFailure(ex)) {
            return [];
        }

        return [.. (ids ?? []).Select(id => (id, Binary(id)))];

        string? Binary(string id) {
            try {
                return manager.UnitBinary(id);
            } catch (Exception ex) when (IsUnitReadFailure(ex)) {
                return null;
            }
        }
    }

    static bool IsUnitReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception;

    /// <summary>
    /// One warning per daemon that `kcap update` does not reach: one running another version than this
    /// CLI, or one whose service unit runs a binary outside this CLI's install. Each names the command
    /// that puts it on this install.
    /// </summary>
    internal static IReadOnlyList<string> DaemonDriftLines(
            string cliVersion, IReadOnlyList<DaemonEntry> daemons, IReadOnlyList<(string Id, string? Binary)> units,
            string? daemonBinary, bool verifySupported) {
        var cli      = CapacitorVersion.Display(cliVersion);
        var versions = daemons.Where(d => d is { Alive: true, Version: not null })
            .ToDictionary(d => d.Name, d => CapacitorVersion.Display(d.Version!));
        var unitBinaries = units.ToDictionary(u => u.Id, u => u.Binary);

        List<string> lines = [];

        foreach (var name in versions.Keys.Union(unitBinaries.Keys).Order(StringComparer.Ordinal)) {
            var drifted = versions.TryGetValue(name, out var version) && version != cli;
            var hasUnit = unitBinaries.TryGetValue(name, out var binary);
            var outside = binary is not null && daemonBinary is not null && !UnitIdentity.SameDaemon(binary, daemonBinary);
            if (!drifted && !outside) continue;

            var newer = drifted && DaemonVersionChange.IsDowngrade(version, cli);
            var older = drifted && DaemonVersionChange.IsDowngrade(cli, version);

            List<string> facts = [];
            if (drifted) facts.Add(newer ? $"daemon '{name}' runs {version}, newer than this kcap's {cli}"
                : older ? $"daemon '{name}' runs {version}, older than this kcap's {cli}"
                : $"daemon '{name}' runs {version}, but this kcap is {cli}");
            if (outside) facts.Add($"{(drifted ? "its service" : $"service '{name}'")} runs {binary}, outside this kcap's install, so `kcap update` does not reach it");

            var fix = !hasUnit
                ? $"`kcap daemon stop --name {name}`, then `kcap daemon start --name {name}`"
                : verifySupported
                    ? $"`kcap daemon service install --replace --verify --name {name}`"
                    : $"`kcap daemon service install --name {name}`";

            lines.Add($"⚠ {string.Join("; ", facts)}. " + (
                newer ? $"Update this kcap to {version} or later; {fix} from this kcap would downgrade the daemon to {cli}."
                : older ? $"Run {fix} to upgrade it to {cli}."
                : $"Run {fix}."));
        }

        return lines;
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
                ? DaemonPidProbe.IsOurDaemon(e.Pid, e.StartToken)
                    ? new DaemonEntry(name, e.Pid, true, DaemonVersionMarker.TryRead(store, name))
                    : new DaemonEntry(name, e.Pid, false)
                : new DaemonEntry(name, null, false));
        }

        return entries;
    }

    /// <param name="Pid">Null when the marker is present but carries no usable PID.</param>
    /// <param name="Version">The version a live daemon recorded at startup, when it recorded one.</param>
    internal sealed record DaemonEntry(string Name, int? Pid, bool Alive, string? Version = null);

    /// <summary>
    /// Whether a wired agent has run a kcap hook on this machine: proof the hooks loaded (and, for
    /// Codex, were trusted), not that a session reached the server. Hooks load only in a new session.
    /// </summary>
    internal static string RecordingLine(HarnessId id, string label, DateTimeOffset? lastEvent, DateTimeOffset now) =>
        lastEvent is { } at
            ? $"{label}: hooks running, last hook event {Ago(now - at)}"
            : id is HarnessId.Codex
                ? $"{label}: no hook event yet. Start a new {label} session and accept its prompt to trust the kcap hooks"
                : $"{label}: no hook event yet. Start a new {label} session";

    /// <summary>Muse has one hook group per lifecycle event, so a hand-edited settings.json can leave
    /// the harness wired with some events missing. Null when every event is present.</summary>
    internal static string? MuseHookGapLine(IReadOnlySet<string> installedEvents) {
        var missing = MuseHooksParser.MuseHookEvents.Where(e => !installedEvents.Contains(e)).ToList();

        return missing.Count == 0
            ? null
            : $"{HarnessRegistry.LabelOf(HarnessId.Muse)}: hooks missing for {string.Join(", ", missing)}. Run `kcap plugin install --muse`";
    }

    /// <summary>The Codex hooks file kcap is wired into but Codex refuses for its byte-order mark, or
    /// null. Read once per status run, so the Hooks line, the detail line and the recording lines
    /// cannot disagree.</summary>
    internal static string? CodexRefusedHooks(HarnessRegistry harnesses) =>
        harnesses.ById(HarnessId.Codex) is CodexHarness { Signals.IsWired: true } codex
     && CodexHooksInstaller.StartsWithByteOrderMark(codex.Paths.UserHooksJson)
            ? codex.Paths.UserHooksJson
            : null;

    /// <summary>Whether a harness's hooks actually run: a Codex file Codex refuses is wired to kcap
    /// yet runs nothing, so it reads as unwired.</summary>
    internal static bool HooksRun(HarnessId id, bool wired, string? codexRefused) =>
        wired && !(id == HarnessId.Codex && codexRefused is not null);

    internal static string CodexHooksDetailLine(string hooksPath) =>
        $"{HarnessRegistry.LabelOf(HarnessId.Codex)}: {hooksPath} starts with a UTF-8 byte-order mark, so Codex ignores every hook in it. Run `kcap plugin install --codex` to rewrite it without one";

    /// <summary>A settings.json that does not parse is reported as such: reading it as "no hooks" would
    /// claim every event is missing, and re-running the install cannot fix it. It is reported for an
    /// installed but unwired Muse too, since an unparseable file is what keeps it from reading as wired;
    /// missing hooks are reported only once wired, where the not-configured line does not already.</summary>
    internal static string? MuseHooksDetailLine(string settingsPath, bool detected, bool wired) {
        if (!detected && !wired) return null;

        var label = HarnessRegistry.LabelOf(HarnessId.Muse);

        try {
            if (!File.Exists(settingsPath)) return null;

            return JsonNode.Parse(File.ReadAllTextShared(settingsPath)) is JsonObject root
                ? wired ? MuseHookGapLine(MuseHooksParser.InstalledEvents(root)) : null
                : $"{label}: {settingsPath} is not a JSON object, so its hooks cannot be read. Fix it, then run `kcap plugin install --muse`";
        } catch (JsonException) {
            return $"{label}: {settingsPath} is not valid JSON, so its hooks cannot be read. Fix it, then run `kcap plugin install --muse`";
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return $"{label}: {settingsPath} could not be read, so its hooks were not checked";
        }
    }

    static string Ago(TimeSpan elapsed) => elapsed switch {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 }   => $"{(int)elapsed.TotalMinutes}m ago",
        { TotalDays: < 2 }    => $"{(int)elapsed.TotalHours}h ago",
        _                     => $"{(int)elapsed.TotalDays}d ago"
    };

    /// <summary>Only a git known to be too old is worth a line: a missing git has nothing to attribute.</summary>
    internal static string? GitHookWarning(Version? git) =>
        git is not null && git < GitHookInstaller.MinimumGit
            ? $"{git.ToString(3)} ignores kcap's commit hook (needs {GitHookInstaller.MinimumGit}+); commits are read from the agent's shell commands"
            : null;

    internal static string IgnoredRepoProfileLine(string profile) =>
        $".kcap.json names profile '{profile}', which is not set up here; recording to the local installation instead";

    /// <summary>
    /// Renders the Hooks status line: every harness, wired or not, in registry order. What "wired"
    /// means is each vendor's own — Gemini merges its hooks into the shared
    /// <c>~/.gemini/settings.json</c>, while Pi and OpenCode track a live-ingest extension file
    /// rather than hooks — but all share the line for at-a-glance parity. Pure: the probing happens
    /// in the caller.
    /// </summary>
    internal static string BuildHooksStatusLine(IEnumerable<(HarnessId Id, bool Wired)> wiring) =>
        string.Join(HooksSeparator, wiring.Select(w => $"{ShortLabel(w.Id)} {(w.Wired ? "✓" : "✗")}"));

    const string HooksSeparator = "  ";

    /// <summary>The width of "  Hooks:   ", and of every continuation row under it.</summary>
    const int HooksIndent = 11;

    /// <summary>Breaks the Hooks line between entries so no row reaches the last column of
    /// <paramref name="width"/>: a terminal wraps an overlong row to column 1, under the labels. A null
    /// width, as for redirected output, leaves the line whole.</summary>
    internal static string WrapHooksLine(string line, int? width) {
        if (width is not { } columns || HooksIndent + line.Length < columns) return line;

        var room = Math.Max(columns - 1 - HooksIndent, 1);
        var rows = new List<string>();
        var row  = "";

        foreach (var entry in line.Split(HooksSeparator)) {
            if (row.Length > 0 && row.Length + HooksSeparator.Length + entry.Length > room) {
                rows.Add(row);
                row = entry;
            } else {
                row = row.Length == 0 ? entry : row + HooksSeparator + entry;
            }
        }

        rows.Add(row);

        return string.Join(Environment.NewLine + new string(' ', HooksIndent), rows);
    }

    static int? TerminalWidth() {
        if (Console.IsOutputRedirected) return null;

        try {
            return Console.WindowWidth > 0 ? Console.WindowWidth : null;
        } catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) {
            return null;
        }
    }

    /// <summary>One line per registered account, but only once some account is not its vendor's
    /// environment-derived directory: those alone are what the Hooks line already reports.</summary>
    internal static string BuildAccountLines(
            IEnumerable<(VendorAccount Account, RecordingState State)> accounts, Func<HarnessId, string> defaultDirectoryOf) {
        var list = accounts.ToList();
        if (list.All(a => AccountDirectory.Same(a.Account.Directory, defaultDirectoryOf(a.Account.Vendor)))) return "";

        return string.Join(Environment.NewLine, list.Select(a =>
            $"  Account: {a.Account.Vendor.ToString().ToLowerInvariant(),-6} {a.Account.Label,-20} {AccountStateLabels.For(a.Account.Vendor, a.State)}"));
    }

    /// <summary>Every vendor shares one line, so the one label carrying a product suffix is
    /// shortened to fit beside the rest.</summary>
    static string ShortLabel(HarnessId id) => id is HarnessId.Claude ? "Claude" : HarnessRegistry.LabelOf(id);
}

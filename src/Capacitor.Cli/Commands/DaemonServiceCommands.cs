using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The <c>kcap daemon service</c> verbs. The OS service manager and the service id are fixed for a
/// whole invocation — <see cref="DispatchAsync"/> resolves both once — so they are state here rather
/// than two arguments threaded through every verb.
/// </summary>
sealed class DaemonServiceCommands(
        DaemonStore store, ConfigRoot root, ProfileContext profiles, IServiceManager manager, string id,
        UserHome home, TimeProvider time) {
    /// <summary>Resolves the OS service manager and the service id once, then runs one verb.</summary>
    public static async Task<int> DispatchAsync(
            DaemonStore store, ConfigRoot root, ProfileContext profiles, UserHome home, TimeProvider time,
            string[] args) {
        if (args.Length == 0) return Usage();

        var action  = args[0];
        var rest    = args[1..];
        var noStart = rest.Contains("--no-start");
        var system  = rest.Contains("--system");

        if (system && action is not ("install" or "uninstall")) return Usage();
        if (system && !OperatingSystem.IsWindows()) {
            await Console.Error.WriteLineAsync("--system installs the Windows service; it does nothing on this OS.");
            return 1;
        }

        var serviceId = DaemonStore.Sanitize(DaemonNameResolver.Resolve(rest, profiles.DaemonName));

        // Elevated first, so the install below already finds the service and enrolls with it. The elevated step
        // changes the machine's service, so a hosted caller is refused ahead of it.
        if (system && action == "install" && OperatingSystem.IsWindows()) {
            var enrolled = new RegistryServiceEnrollmentStore().Read()?.Name;
            // A malformed --retire names no target here; the install below refuses it
            if (await RefuseHostedCallerAsync(HostsCallerOf(store), serviceId, ParseRetire(rest).Id, enrolled, rest)) return 1;

            var elevated = WindowsServiceElevation.Run("install");
            if (elevated != 0) return await ElevationFailedAsync(elevated, "install");
        }

        IServiceManager manager;
        try {
            manager = ServiceManagerFactory.ForCurrentOs(root, home, time);
        } catch (PlatformNotSupportedException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var verbs = new DaemonServiceCommands(store, root, profiles, manager, serviceId, home, time);

        // An update of the service binary: when the daemon is already enrolled as asked, the elevated step was the
        // whole job. A changed profile or agent cap goes through the install below, which re-enrolls.
        if (system && action == "install" && manager is WindowsServiceManager windows && EnrolledAsAsked(windows)) {
            await Console.Out.WriteLineAsync($"kcap-service is current; '{serviceId}' is already enrolled with it.");
            return 0;
        }

        bool EnrolledAsAsked(WindowsServiceManager windows) {
            try {
                return windows.Query(serviceId) is { Probe: LabelProbe.Loaded, UnitPresent: true }
                    && windows.EnrolledAs(serviceId,
                        DaemonCommands.ExtractFlagValue(rest, "--profile") ?? profiles.Resolution.ProfileName,
                        DaemonCommands.ExtractFlagValue(rest, "--max-agents"));
            } catch (ArgumentException) {
                return false;
            }
        }

        return action switch {
            "install"   => await verbs.Install(rest, startNow: !noStart),
            "uninstall" when system && OperatingSystem.IsWindows() => await verbs.UninstallSystem(),
            "uninstall" => UnderArg(rest) switch {
                null    => await verbs.Uninstall(),
                ""      => Usage(),
                var dir => await verbs.UninstallUnder(dir),
            },
            "start"     => await verbs.Start(rest),
            "stop"      => await verbs.Stop(rest),
            "ensure"    => await verbs.Ensure(rest),
            "status"    => rest.Contains("--json") ? await verbs.StatusJson() : await verbs.Status(),
            "refresh"   => await verbs.Refresh(force: rest.Contains("--force")),
            _           => Usage(),
        };
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static async Task<int> ElevationFailedAsync(int exit, string verb) {
        await Console.Error.WriteLineAsync(exit == WindowsServiceElevation.DeclinedExit
            ? "The administrator prompt was declined; the Windows service was not changed."
            : $"kcap-service {verb} failed (exit {exit}); the Windows service was not changed.");
        return exit;
    }

    /// <summary>Unenrolls this user, then removes the machine's service behind the UAC prompt. The service's
    /// other users keep their daemons running, unsupervised, until they install again</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal async Task<int> UninstallSystem() {
        if (manager is WindowsServiceManager) {
            var unenrolled = await Uninstall();
            if (unenrolled != 0) return unenrolled;
        }
        var elevated = WindowsServiceElevation.Run("uninstall");
        if (elevated != 0) return await ElevationFailedAsync(elevated, "uninstall");
        await Console.Out.WriteLineAsync("The Windows service kcap-service is uninstalled.");
        return 0;
    }

    /// <summary>
    /// Runs the ensure ladder for the browser flow, or <b>null on a platform with no service
    /// manager</b> — the caller renders that as its own refusal rather than as a failed transaction.
    /// </summary>
    /// <param name="previousName">The daemon name the profile carried before this run saved its own; its
    /// service, when installed, is retired in the same transaction as the install.</param>
    internal static async Task<FlowEnsure?> FlowEnsureAsync(
            ConfigRoot root, ProfileContext profiles, UserHome home, TimeProvider time, string? previousName = null) {
        DaemonServiceCommands verbs;

        try {
            verbs = ForFlow(root, profiles, home, time);
        } catch (PlatformNotSupportedException) {
            return null;
        }

        return await verbs.EvaluateFlowEnsureAsync(profiles.Resolution.ProfileName, previousName);
    }

    /// <summary>
    /// Gives the flow's service unit the search path this process sees when a harness CLI is found on it
    /// but not on the unit's, so the daemon finds what setup just found. Null when nothing needed it or the
    /// platform has no service manager this can rewrite.
    /// </summary>
    internal static async Task<PathRefresh?> FlowRefreshPathAsync(
            ConfigRoot root, ProfileContext profiles, UserHome home, TimeProvider time, HarnessRegistry harnesses) {
        DaemonServiceCommands verbs;

        try {
            verbs = ForFlow(root, profiles, home, time);
        } catch (PlatformNotSupportedException) {
            return null;
        }

        return await verbs.RefreshStalePathAsync(harnesses);
    }

    /// <summary>Held from reading the unit's PATH to the last write, so a concurrent install's unit is never
    /// merged into or restored over.</summary>
    async Task<PathRefresh?> RefreshStalePathAsync(HarnessRegistry harnesses) {
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, ForcedLockWait, time);
        if (txn is null) return new(UnitRefresh.Contended);

        if (manager.UnitEnvironment(id) is not { } env) return null;

        var path = ServicePath.Refreshed(env.GetValueOrDefault("PATH"), Environment.GetEnvironmentVariable("PATH"), harnesses);

        return path is null ? null : await RefreshPathAsync(path);
    }

    /// <summary>launchd reads a plist only when the job loads, so the rewrite takes a reload that keeps the
    /// daemon's agents for it to resume; systemd reads the unit at the next start, which the daemon is asked
    /// for once idle.</summary>
    async Task<PathRefresh> RefreshPathAsync(string path) {
        switch (manager) {
            case LaunchdServiceManager launchd: {
                var started = time.GetTimestamp();
                try {
                    var outcome = launchd.RefreshUnit(
                        id, () => Proceeds(StopForReload(id, "now", whenIdle: true)), () => RefreshDeadline - time.GetElapsedTime(started),
                        out var error, amend: plist => LaunchdUnit.WithEnvironmentValue(plist, "PATH", path));
                    return new(outcome, error);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
                    return new(UnitRefresh.Failed, ex.Message);
                }
            }
            case SystemdServiceManager systemd:
                try {
                    if (systemd.SetEnvironment(id, "PATH", path) is not { } previous) return new(UnitRefresh.Current);
                    switch (await RequestRestartAsync("when-idle")) {
                        case "restarting": return new(UnitRefresh.Reloaded);
                        case "queued":     return new(UnitRefresh.NotLoaded);
                    }
                    if (manager.Query(id).State != ServiceState.Running) return new(UnitRefresh.NotLoaded);

                    // A running daemon that took no restart keeps the old PATH; the unit must say so too, or
                    // the next run reads the rewrite as applied.
                    systemd.RestoreUnit(id, previous);
                    return new(UnitRefresh.Deferred);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
                    return new(UnitRefresh.Failed, ex.Message);
                }
            default:
                return new(UnitRefresh.UnitUnsupported);
        }
    }

    async Task<string?> RequestRestartAsync(string mode) {
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            return await DaemonRestartClient.RequestAsync(store, id, mode, cts.Token) is { Type: FrameType.RestartAck } reply ? reply.Text : null;
        } catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) {
            return null;
        }
    }

    Task<FlowEnsure> EvaluateFlowEnsureAsync(string? profileName, string? previousName) =>
        RetiringEnsureAsync(id, previousName, manager.Query,
            async retire => (await EvaluateEnsureAsync(profileName, retire)).Result);

    /// <summary>
    /// The ensure ladder for <paramref name="id"/>, retiring the service <paramref name="previousName"/>
    /// left installed when the platform's verify engine can. Only an installed unit is a candidate: a
    /// daemon started by hand has none, and the engine refuses one pinned to another profile.
    ///
    /// <para>A refused or contended retire changed nothing, so the ladder runs again without it: the name
    /// setup saved still gets its daemon. The old service is reported as left installed only once the new
    /// one is up; before that it is the only daemon the machine has.</para>
    /// </summary>
    internal static async Task<FlowEnsure> RetiringEnsureAsync(
            string id, string? previousName, Func<string, ServiceQuery> query,
            Func<string?, Task<ServiceEnsureJson>> ensure) {
        var previous = string.IsNullOrWhiteSpace(previousName) ? null : DaemonStore.Sanitize(previousName);

        if (previous is null || previous == id || !HasUnit(query(previous))) return new(await ensure(null));

        var result = await ensure(previous);
        if (result.Reason is "verify_retire_refused" or "verify_contended") result = await ensure(null);

        var after = query(previous);

        return HasUnit(after) ? (FlowEnsure.IsUp(result) ? new(result, LeftInstalled: previous) : new(result))
            : after.Probe == LabelProbe.Absent ? new(result, Retired: previous)
            : new(result);

        static bool HasUnit(ServiceQuery q) => q.Probe == LabelProbe.Loaded || q.UnitPresent;
    }

    /// <summary>The flow's own resolution of the manager and the service id. The id is the name setup
    /// just saved, ahead of <c>KCAP_DAEMON_NAME</c>: setup chose it to be free, and an environment name
    /// it moved off would install a second service under the held one.</summary>
    static DaemonServiceCommands ForFlow(
            ConfigRoot root, ProfileContext profiles, UserHome home, TimeProvider time) =>
        new(DaemonStore.FromEnvironment(), root, profiles, ServiceManagerFactory.ForCurrentOs(root, home, time),
            DaemonStore.Sanitize(profiles.DaemonName ?? DaemonNameResolver.Resolve([], null)), home, time);

    /// <summary>
    /// The <c>ExtraArgs</c> baked into a service unit, from the raw <c>--max-agents</c> flag value.
    ///
    /// <para>Validated here rather than only escaped at the unit writers. This is the ONLY caller-supplied
    /// entry in <c>ExtraArgs</c>, it arrives unparsed off the command line, and it is persisted into a file
    /// the OS executes at every logon — so a value that is not the integer the flag documents has no
    /// legitimate reading, and accepting one turns a typo into a durable startup artifact. Review raised the
    /// sink; this is the matching narrowing at the source, so the two are independent.</para>
    /// </summary>
    internal static List<string> ExtraArgs(string? maxAgentsFlag) {
        if (maxAgentsFlag is null) return [];

        if (!int.TryParse(maxAgentsFlag, out var maxAgents) || maxAgents < 0)
            throw new ArgumentException($"--max-agents must be 0 (unlimited) or a positive integer (got '{maxAgentsFlag}').");

        return ["--max-agents", maxAgents.ToString()];
    }

    /// <summary>Above <see cref="LaunchdServiceManager.ReloadBudget"/> and short of the refresh step's own
    /// timeout, so the last reload it starts can finish.</summary>
    static readonly TimeSpan RefreshDeadline = TimeSpan.FromSeconds(85);

    /// <summary>Stops a running daemon for a reload. Test seam; production uses the control socket.</summary>
    internal Func<string, ReloadStop>? ReloadStopper { get; init; }

    /// <summary>Whether the daemon of a service id hosts this process. Test seam; production asks <see cref="HostedCaller"/>.</summary>
    internal Func<string, bool>? HostsCaller { get; init; }

    static Func<string, bool> HostsCallerOf(DaemonStore store) => serviceId => HostedCaller.HostedBy(store, serviceId);

    /// <summary>Refuses an install whose service <paramref name="id"/>, retired daemon or displaced enrollment hosts
    /// this process, naming the command to run elsewhere: replacing that daemon would end this command with it.</summary>
    /// <param name="enrolledId">This user's <c>kcap-service</c> enrollment. The service holds one per user and stops
    /// the daemon of the one an install replaces.</param>
    static async Task<bool> RefuseHostedCallerAsync(
            Func<string, bool> hostsCaller, string id, string? retireId, string? enrolledId, string[] args) {
        if (new[] { id, retireId, enrolledId }.FirstOrDefault(target => target is not null && hostsCaller(target)) is not { } host) return false;

        List<string> words = ["kcap", "daemon", "service", "install", .. args];
        if (!args.Contains("--name")) words.AddRange(["--name", id]);
        await Console.Error.WriteLineAsync(HostedCaller.Refusal(host, $"`{string.Join(' ', words)}`"));

        return true;
    }

    /// <summary>The sanitized <c>--retire</c> target, or the refusal for a malformed one</summary>
    internal static (string? Id, string? Error) ParseRetire(string[] args) {
        string? retire;
        try {
            retire = DaemonCommands.ExtractFlagValue(args, "--retire");
        } catch (ArgumentException ex) {
            return (null, ex.Message);
        }
        if (retire is null) return (null, null);

        // Sanitize maps a blank or all-invalid value to "daemon", the fallback name that usually shares the
        // profile: a raw value that is not itself a spelling of "daemon" is a typo aimed at the wrong target.
        var retireId = DaemonStore.Sanitize(retire);
        return retireId == "daemon" && !string.Equals(retire.Trim(), "daemon", StringComparison.OrdinalIgnoreCase)
            ? (null, $"--retire value '{retire}' is not a service id.")
            : (retireId, null);
    }

    static readonly TimeSpan ForcedLockWait = TimeSpan.FromSeconds(10);

    internal static string RefreshToken(UnitRefresh outcome) => outcome switch {
        UnitRefresh.Reloaded        => "reloaded",
        UnitRefresh.Current         => "current",
        UnitRefresh.NotLoaded       => "not_loaded",
        UnitRefresh.Deferred        => "deferred",
        UnitRefresh.Contended       => "contended",
        UnitRefresh.Unverified      => "unverified",
        UnitRefresh.UnitMissing     => "unit_missing",
        UnitRefresh.UnitUnreadable  => "unit_unreadable",
        UnitRefresh.UnitUnsupported => "unit_unsupported",
        _                           => "failed",
    };

    internal static int ForcedExitCode(UnitRefresh outcome) => outcome is UnitRefresh.Reloaded or UnitRefresh.Current ? 0 : 1;

    static string ForceHint(string serviceId) =>
        $"To apply it now, run `kcap daemon service refresh --name {serviceId} --force`.";

    /// <summary>
    /// Brings every installed unit up to the one this version writes, and moves a unit pinned to a script
    /// install's version directory onto its stable <c>current</c> path so the daemon follows updates. A
    /// launchd reload stops the daemon with its agents kept for the reloaded one to resume, and is left for
    /// a later run when the daemon does not accept the stop; a systemd daemon picks the new path up on its next start.
    /// </summary>
    internal async Task<int> Refresh(bool force = false, Func<string, string>? stabilize = null) {
        stabilize ??= InstallPaths.Stabilize;

        if (force && manager is not LaunchdServiceManager) {
            await Console.Error.WriteLineAsync("refresh_outcome=unsupported");
            await Console.Error.WriteLineAsync($"Daemon '{id}': --force reloads a launchd service; this platform's units are refreshed without it.");
            return 1;
        }

        if (manager is SystemdServiceManager systemd) {
            var unwritten = 0;

            foreach (var serviceId in systemd.ListInstalled()) {
                try {
                    if (systemd.Repoint(serviceId, stabilize))
                        await Console.Out.WriteLineAsync($"Daemon '{serviceId}': now runs the current kcap version on its next start.");
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
                    await Console.Error.WriteLineAsync($"Daemon '{serviceId}': could not update its unit ({ex.Message}).");
                    unwritten++;
                }
            }

            return unwritten == 0 ? 0 : 1;
        }

        if (manager is not LaunchdServiceManager launchd) return 0;
        return force ? await RefreshForcedAsync(launchd, stabilize) : await RefreshAllAsync(launchd, stabilize);
    }

    /// <summary>The unattended post-update pass: every installed unit, never a reload of a daemon that did not accept the stop.</summary>
    async Task<int> RefreshAllAsync(LaunchdServiceManager launchd, Func<string, string> stabilize) {
        var failed  = 0;
        var started = time.GetTimestamp();
        TimeSpan TimeLeft() => RefreshDeadline - time.GetElapsedTime(started);

        foreach (var serviceId in launchd.ListInstalled()) {
            if (TimeLeft() < LaunchdServiceManager.RefreshCtlTimeout) {
                await Console.Out.WriteLineAsync("Out of time; the remaining daemons are checked on the next update.");
                break;
            }

            using var txn = await ServiceTxnLock.TryAcquireAsync(store, serviceId, TimeSpan.Zero, time);
            if (txn is null) {
                await Console.Out.WriteLineAsync($"Daemon '{serviceId}': another service operation is in progress, so its service unit change waits for the next update.");
                continue;
            }

            ReloadStop? stop = null;
            var outcome = launchd.RefreshUnit(
                serviceId, () => Proceeds((stop = StopForReload(serviceId, "now", whenIdle: true)).Value), TimeLeft, out var error, stabilize);

            switch (outcome) {
                case UnitRefresh.Reloaded:
                    await Console.Out.WriteLineAsync($"Daemon '{serviceId}': reloaded with its updated service unit.{OverrunNote(stop)}");
                    break;
                case UnitRefresh.Deferred:
                    await Console.Out.WriteLineAsync(
                        $"Daemon '{serviceId}': {DeferralReason(stop)}, so its service unit change waits for the next update. {ForceHint(serviceId)}");
                    break;
                case UnitRefresh.Unverified:
                    await Console.Out.WriteLineAsync(
                        $"Daemon '{serviceId}': launchd's state could not be read, so the check waits for the next update.");
                    break;
                case UnitRefresh.Failed:
                    await Console.Error.WriteLineAsync($"Daemon '{serviceId}': {error}");
                    failed++;
                    break;
            }
        }

        return failed == 0 ? 0 : 1;
    }

    /// <summary>One daemon, a reload, and one <c>refresh_outcome=</c> line for machine callers.</summary>
    async Task<int> RefreshForcedAsync(LaunchdServiceManager launchd, Func<string, string> stabilize) {
        UnitRefresh outcome;
        string?     error = null;
        ReloadStop? stop  = null;
        try {
            using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, ForcedLockWait, time);
            if (txn is null) {
                outcome = UnitRefresh.Contended;
            } else {
                var started = time.GetTimestamp(); // the lock wait is not charged to the reload budget
                outcome = launchd.RefreshUnit(
                    id, () => Proceeds((stop = StopForReload(id, "force", whenIdle: false)).Value), () => RefreshDeadline - time.GetElapsedTime(started),
                    out error, stabilize, requirePositiveSpawnType: true);
            }
        } catch (Exception ex) {
            outcome = UnitRefresh.Failed;
            error   = ex.Message;
        }

        await Console.Error.WriteLineAsync($"refresh_outcome={RefreshToken(outcome)}");
        if (error is not null) await Console.Error.WriteLineAsync($"Daemon '{id}': {error}");
        await Console.Out.WriteLineAsync(outcome switch {
            UnitRefresh.Reloaded        => $"Daemon '{id}': reloaded at standard priority.{OverrunNote(stop)}",
            UnitRefresh.Current         => $"Daemon '{id}': already runs at standard priority.",
            UnitRefresh.NotLoaded       => $"Daemon '{id}': its service is not loaded; run `kcap daemon service start --name {id}`.",
            UnitRefresh.Deferred        => $"Daemon '{id}': the daemon {DeferralReason(stop)}.",
            UnitRefresh.Contended       => $"Daemon '{id}': another service operation is in progress. Try again shortly.",
            UnitRefresh.Unverified      => $"Daemon '{id}': launchd's state could not be verified.",
            UnitRefresh.UnitMissing     => $"Daemon '{id}': no service unit is installed.",
            UnitRefresh.UnitUnreadable  => $"Daemon '{id}': the service unit cannot be read.",
            UnitRefresh.UnitUnsupported => $"Daemon '{id}': the service unit cannot be brought to Standard; reinstall it with `kcap daemon service install --replace --verify --name {id}`.",
            _                           => $"Daemon '{id}': the reload failed.",
        });
        return ForcedExitCode(outcome);
    }

    static bool Proceeds(ReloadStop stop) => stop is ReloadStop.Exited or ReloadStop.Overran or ReloadStop.Restarting;

    static string OverrunNote(ReloadStop? stop) =>
        stop == ReloadStop.Overran ? " It was still stopping at the end of the wait and was ended by the reload; the reloaded daemon resumes its agents." : "";

    static string DeferralReason(ReloadStop? stop) => stop switch {
        ReloadStop.NoAnswer       => "did not answer the stop request",
        ReloadStop.RestartRefused => "is busy",
        _                         => "did not stop for the reload",
    };

    /// <summary>
    /// Asks the daemon to exit with its agents kept for the reloaded daemon to resume. Once it has accepted,
    /// the reload goes ahead even when it is still exiting at the end of the wait: it marked its agents for
    /// resume when it began stopping, and the bootout ends what is left. A daemon that predates the shutdown
    /// frame closes the connection unanswered; it is asked for a restart in <paramref name="legacyMode"/>
    /// instead, and keeps no resume ledger to lose. <paramref name="whenIdle"/> leaves a daemon running an
    /// eval or admitting a launch alone.
    /// </summary>
    ReloadStop StopForReload(string serviceId, string legacyMode, bool whenIdle) {
        if (ReloadStopper is { } stopper) return stopper(serviceId);

        var outcome = DaemonShutdownClient.StopAsync(
                store, serviceId, new ShutdownRequestDto("keep", "final", whenIdle), LaunchdServiceManager.ReloadExitWait, time,
                CancellationToken.None)
            .GetAwaiter().GetResult();

        return outcome switch {
            ShutdownOutcome.Exited  => ReloadStop.Exited,
            ShutdownOutcome.Overran => ReloadStop.Overran,
            ShutdownOutcome.Busy    => ReloadStop.RestartRefused,
            ShutdownOutcome.Refused => RequestLegacyRestart(serviceId, legacyMode) ? ReloadStop.Restarting : ReloadStop.RestartRefused,
            _                       => ReloadStop.NoAnswer,
        };
    }

    /// <summary><c>now</c> is applied only while idle, <c>force</c> ends whatever the daemon hosts. A
    /// <c>queued</c> answer is not an acceptance.</summary>
    bool RequestLegacyRestart(string serviceId, string mode) {
        try {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            var reply = DaemonRestartClient.RequestAsync(store, serviceId, mode, cts.Token).GetAwaiter().GetResult();
            return reply is { Type: FrameType.RestartAck, Text: "restarting" };
        } catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException) {
            return false;
        }
    }

    internal async Task<int> Install(string[] args, bool startNow) {
        var verify  = args.Contains("--verify");
        var replace = args.Contains("--replace");

        // --no-start withholds the start; --verify's whole job is to prove the started daemon is
        // ready. The two contradict — reject rather than silently start anyway (or silently skip
        // verification).
        if (verify && !startNow) {
            await Console.Error.WriteLineAsync("--no-start is incompatible with --verify.");
            return 1;
        }

        // --replace only has meaning inside the verify transaction engine (it selects the
        // ownership matrix in ServiceVerify.InstallVerifiedAsync) — a plain (non-verified) install
        // has no transaction to hand it to.
        if (replace && !verify) {
            await Console.Error.WriteLineAsync("install --replace requires --verify.");
            return 1;
        }

        var (retireId, retireError) = ParseRetire(args);
        if (retireError is not null) {
            await Console.Error.WriteLineAsync(retireError);
            return 1;
        }

        if (retireId is not null && !(replace && verify)) {
            await Console.Error.WriteLineAsync("install --retire requires --replace --verify.");
            return 1;
        }

        if (retireId is not null && retireId == id) {
            await Console.Error.WriteLineAsync("--retire names the service being installed; nothing to retire.");
            return 1;
        }

        var enrolled = manager is WindowsServiceManager && manager.ListInstalled() is [var first, ..] ? first : null;
        if (await RefuseHostedCallerAsync(HostsCaller ?? HostsCallerOf(store), id, retireId, enrolled, args)) return 1;

        var daemonPath = UnitIdentity.ResolveDaemonBinary();
        if (daemonPath is null) { await Console.Error.WriteLineAsync(DaemonCommands.DaemonNotFoundMessage()); return 1; }

        var profileName = DaemonCommands.ExtractFlagValue(args, "--profile") ?? profiles.Resolution.ProfileName;
        var env = new Dictionary<string, string>(ServiceEnvironment.Capture(profileName, root, home)) {
            ["KCAP_DAEMON_SUPERVISED"] = id,   // name-specific; daemon honors it only when == its sanitized --name
        };

        List<string> extra;
        try {
            extra = ExtraArgs(DaemonCommands.ExtractFlagValue(args, "--max-agents"));
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var logPath = root.Path($"daemon-{id}.log");
        var spec    = new ServiceSpec(id, daemonPath, logPath, env, extra);

        if (verify) {
            // The CLI is the safety boundary for §4.1's precondition: prove the pinned profile resolves
            // to a valid server URL here too, so the transaction never destroys a working unit only to
            // install one whose daemon would exit config-invalid and never satisfy readiness.
            var profileUrlValid = await ServiceInstallViability.PinnedProfileServerUrlValidAsync(env, root);
            var exit = manager is LaunchdServiceManager launchd
                ? await new ServiceVerify(store, root, launchd,
                        n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, time, t),
                        time, profileViable: () => profileUrlValid, gateEnv: Environment.GetEnvironmentVariable)
                    .InstallVerifiedAsync(spec, replace: replace, CapacitorVersion.Current(), retireServiceId: retireId)
                : await NewPollingVerify(() => profileUrlValid)
                    .InstallVerifiedAsync(spec, replace: replace, CapacitorVersion.Current(), retireServiceId: retireId);
            if (exit != VerifyExit.Ok) return exit;
        } else {
            // Plain (non-verify) install serializes on the same per-label lock every other mutating
            // verb takes — a terminal install must not race an app-driven --replace --verify.
            var exit = await InstallPlain(spec, startNow);
            if (exit != 0) return exit;
        }

        // Closed-stdio tolerance for the entire success tail (not just the first line): the npm
        // grandchild shares the GUI's pipes, and by this point the real mutation already happened
        // — a broken pipe on any of these purely informational lines must never turn an
        // already-successful install into a crash/non-zero exit.
        try {
            if (verify) {
                await Console.Out.WriteLineAsync($"Service '{id}' installed (verified, {manager.Describe()}).");
                await Console.Out.WriteLineAsync($"  Daemon:    {CapacitorVersion.CurrentDisplay()} is running; the unit starts {daemonPath}");
            } else {
                await Console.Out.WriteLineAsync($"Service '{id}' installed ({manager.Describe()}).");
                await Console.Out.WriteLineAsync("  Auto-restarts on crash/SIGKILL; starts at login.");
            }

            // A reviewer switch frozen into a unit is worth saying out loud: the unit outlives the shell it
            // was captured from, so an operator who later changes the variable has no effect until they
            // reinstall. The line must state what the captured VALUE does, not assume it enables — since
            // these became opt-outs, a captured `0` DISABLES, and the old "the reviewer it enables stays on"
            // text was exactly backwards for that case. Classified through the same Core parser the daemon
            // reads, so the notice and the daemon can never disagree about a value's meaning.
            // The hosted-agy trio's state, said out loud for the same reason the consent flags are:
            // the unit outlives the shell, and a partial trio is never a working agy configuration —
            // silence here is how a reinstall that changed nothing goes unnoticed.
            var (agyPresent, agyMissing) = Capacitor.Cli.Harness.Antigravity.AntigravityAdcTrio.Status(env);
            if (agyPresent && agyMissing.Count == 0) {
                await Console.Out.WriteLineAsync(
                    "  Antigravity: ADC trio captured into the unit — hosted agy can authenticate.");
            } else if (agyPresent) {
                await Console.Out.WriteLineAsync(
                    $"  Antigravity: PARTIAL ADC capture — missing {string.Join(", ", agyMissing)}. "
                  + "Hosted agy cannot authenticate until the unit carries all three "
                  + "(run `gcloud auth application-default login`, then reinstall).");
            }

            foreach (var flag in ServiceEnvironment.CarriedConsentFlags(env)) {
                var effect = ReviewerConsent.IsEnabled(env[flag])
                    ? "keeps that reviewer ENABLED (already the default)"
                    : "DISABLES that reviewer";
                await Console.Out.WriteLineAsync(
                    $"  Reviewer:  {flag}={env[flag]} captured into the unit — {effect} for this service "
                  + "until you reinstall with a different value.");
            }

            await Console.Out.WriteLineAsync($"  Log:       {logPath}");
            await Console.Out.WriteLineAsync($"  Stop:      kcap daemon service stop --name {id}");
            await Console.Out.WriteLineAsync($"  Remove:    kcap daemon service uninstall --name {id}");
        } catch (IOException) { }

        return 0;
    }

    /// <summary>Plain install under the per-label service lock, matching stop/start/uninstall:
    /// null lock → the same coded-contention message, exit 1, without calling <c>Install</c>. Internal so
    /// the lock-contention path is testable without <see cref="UnitIdentity.ResolveDaemonBinary"/> in the loop.</summary>
    internal async Task<int> InstallPlain(ServiceSpec spec, bool startNow) {
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, spec.ServiceId, TimeSpan.FromSeconds(10), time);

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{spec.ServiceId}'. Try again shortly.");
            return 1;
        }

        var before = TryQuery(spec.ServiceId);
        if (manager.Install(spec, startNow, out var error)) return 0;

        await Console.Error.WriteLineAsync(InstallFailure(spec, manager.Describe(), error, before, TryQuery(spec.ServiceId)));
        return 1;
    }

    ServiceQuery? TryQuery(string serviceId) {
        try {
            return manager.Query(serviceId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>Why a plain install failed. When the unit was already loaded: whether it still is, which
    /// daemon its unit names, and the command that reinstalls it. <paramref name="after"/> is read after
    /// the failure, because the install may already have stopped the old job.</summary>
    internal static string InstallFailure(
            ServiceSpec spec, string describe, string? error, ServiceQuery? before, ServiceQuery? after) {
        var id      = spec.ServiceId;
        var message = $"Could not install service '{id}' ({describe}): {error}";
        if (before is not { Probe: LabelProbe.Loaded }) return message;

        var reinstall = $"`kcap daemon service install --replace --verify --name {id}`";

        var unit = after?.UnitPresent == true ? after : before;
        var ours = UnitIdentity.SameDaemon(unit.BinaryPath, spec.DaemonBinaryPath);
        var runs = ours
            ? $"this install's daemon ({spec.DaemonBinaryPath})"
            : $"{unit.BinaryPath ?? "a daemon binary kcap could not read"}, not this install's daemon ({spec.DaemonBinaryPath})";

        var detail = after?.Probe switch {
            LabelProbe.Loaded => $"The service is still loaded, and its unit names {runs}.",
            LabelProbe.Absent => $"The service was loaded and is now stopped; its unit names {runs}.",
            _ => $"The service was loaded; kcap could not tell whether it still is (see `kcap daemon service status --name {id}`). Its unit names {runs}.",
        };
        var fix = ours && after?.Probe == LabelProbe.Loaded
            ? $"To reinstall it in place, run {reinstall}."
            : ours ? $"To install it again, run {reinstall}." : $"To point it at this install, run {reinstall}.";

        return $"{message}{Environment.NewLine}  {detail} {fix}";
    }

    internal async Task<int> Uninstall() {
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, TimeSpan.FromSeconds(10), time);

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{id}'. Try again shortly.");
            return 1;
        }

        var ended = await ServiceRemoval.EndAgentsAsync(store, id, time);

        if (!manager.Uninstall(id, out var error)) {
            await Console.Error.WriteLineAsync($"Could not uninstall service '{id}': {error}");
            return 1;
        }

        if (!ended) DaemonCommands.MarkLedgerInterrupted(store, id, time, Console.Error);

        await Console.Out.WriteLineAsync($"Service '{id}' uninstalled ({manager.Describe()}).");
        return 0;
    }

    /// <summary>
    /// Removes every installed unit whose daemon binary lies under <paramref name="dir"/>, and no
    /// other: a desktop app's uninstaller owns only the services that run its own copy, never one an
    /// npm or standalone install wrote.
    /// </summary>
    internal async Task<int> UninstallUnder(string dir) {
        if (manager.TryListInstalled() is not { } units) {
            await Console.Error.WriteLineAsync($"Could not list the installed services ({manager.Describe()}); nothing was uninstalled.");
            return 1;
        }

        var failed = false;

        foreach (var unit in units) {
            try {
                if (!Owned(unit)) continue;

                using var txn = await ServiceTxnLock.TryAcquireAsync(store, unit, TimeSpan.FromSeconds(5), time);
                if (txn is null) {
                    await Console.Error.WriteLineAsync($"Another service operation is in progress for '{unit}'. Try again shortly.");
                    failed = true;
                } else if (!Owned(unit)) {
                    // An install holding the lock may have repointed the unit at another copy of kcap.
                    continue;
                } else if (manager.Query(unit).Probe == LabelProbe.Unknown) {
                    // Ending the daemon first would leave it stopped beside a unit the supervisor cannot remove.
                    await Console.Error.WriteLineAsync($"Could not uninstall service '{unit}': {manager.Describe()} did not report its state; its daemon was left running.");
                    failed = true;
                } else {
                    var ended = await ServiceRemoval.EndAgentsAsync(store, unit, time);

                    if (!manager.Uninstall(unit, out var error)) {
                        await Console.Error.WriteLineAsync($"Could not uninstall service '{unit}': {error}");
                        failed = true;
                    } else {
                        if (!ended) DaemonCommands.MarkLedgerInterrupted(store, unit, time, Console.Error);
                        await Console.Out.WriteLineAsync($"Service '{unit}' uninstalled ({manager.Describe()}).");
                    }
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
                await Console.Error.WriteLineAsync($"Could not uninstall service '{unit}': {ex.Message}");
                failed = true;
            }
        }

        return failed ? 1 : 0;

        bool Owned(string unit) =>
            manager.Status(unit).BinaryPath is { } binary && IsUnder(binary, dir, OperatingSystem.IsWindows());
    }

    /// <summary>
    /// The folder <c>--under</c> names: null without the flag, empty when its value is missing,
    /// blank or another flag.
    /// </summary>
    internal static string? UnderArg(string[] rest) {
        var at = Array.IndexOf(rest, "--under");
        if (at < 0) return null;
        var value = at + 1 < rest.Length ? rest[at + 1] : "";
        return string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) ? "" : value;
    }

    internal static bool IsUnder(string path, string dir, bool ignoreCase) {
        // A filesystem root keeps its separator through the trim.
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        var root = Path.EndsInDirectorySeparator(trimmed) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>Routes plain vs <c>--verify</c> starts: launchd through its transaction engine, every
    /// other manager through <see cref="PollingServiceVerify"/>.</summary>
    internal async Task<int> Start(string[] args) {
        if (!args.Contains("--verify")) return await StartPlain();
        if (manager is LaunchdServiceManager) return await StartVerified();

        var exit = await NewPollingVerify().StartVerifiedAsync(id);
        if (exit == VerifyExit.Ok) {
            try { await Console.Out.WriteLineAsync($"Service '{id}' started (verified)."); } catch (IOException) { }
        }
        return exit;
    }

    PollingServiceVerify NewPollingVerify(Func<bool>? profileViable = null) =>
        new(store, manager, n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, time, t), time,
            profileViable, endsManualOwner: OperatingSystem.IsWindows());

    async Task<int> StartPlain() {
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, TimeSpan.FromSeconds(10), time);

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{id}'. Try again shortly.");
            return 1;
        }

        if (!manager.Start(id, out var error)) {
            await Console.Error.WriteLineAsync($"Could not start service '{id}': {error}");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Service '{id}' started.");
        return 0;
    }

    /// <summary>
    /// <c>--verify</c>: hands off to the <see cref="ServiceVerify"/> transaction engine, which
    /// acquires the <see cref="ServiceTxnLock"/> itself — no double-acquire here.
    /// </summary>
    async Task<int> StartVerified() {
        var engine = new ServiceVerify(store, root, (LaunchdServiceManager)manager,
            n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, time, t), time,
            gateEnv: Environment.GetEnvironmentVariable);
        var exit = await engine.StartVerifiedAsync(id);

        // Same closed-stdio tolerance as the engine's own Say: a broken pipe on this purely
        // informational line must not turn an already-successful verified start into a crash.
        if (exit == VerifyExit.Ok) {
            try { await Console.Out.WriteLineAsync($"Service '{id}' started (verified)."); }
            catch (IOException) { }
        }

        return exit;
    }

    internal async Task<int> Stop(string[] args) {
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, TimeSpan.FromSeconds(10), time);

        if (txn is null) {
            await Console.Error.WriteLineAsync($"Another service operation is in progress for '{id}'. Try again shortly.");
            return 1;
        }

        var request = new ShutdownRequestDto(args.Contains("--keep-agents") ? "keep" : "end", "final");
        var outcome = DaemonPidProbe.ValidatedPid(store, id) is null
            ? ShutdownOutcome.Exited
            : await DaemonShutdownClient.StopAsync(store, id, request, DaemonCommands.StopExitWait, time, CancellationToken.None);
        var exited  = outcome == ShutdownOutcome.Exited || DaemonPidProbe.ValidatedPid(store, id) is null;

        if (!manager.Stop(id, out var error)) {
            await Console.Error.WriteLineAsync($"Could not stop service '{id}': {error}");
            return 1;
        }

        if (!exited) DaemonCommands.MarkLedgerInterrupted(store, id, time, Console.Error, DaemonCommands.KeepsSavedResumes(request, outcome));

        await Console.Out.WriteLineAsync($"Service '{id}' stopped (still installed).");
        return 0;
    }

    internal async Task<int> Status() {
        await StaleTxnMarker.SettleAsync(store, manager, id, time);
        ServiceStatus status;
        try {
            status = manager.Status(id);
        } catch (InvalidOperationException ex) {
            await Console.Error.WriteLineAsync($"Could not read the state of service '{id}' ({manager.Describe()}): {ex.Message}");
            return 1;
        }
        await Console.Out.WriteLineAsync($"Service '{id}': {status.State} ({manager.Describe()})");
        if (status.BinaryPath is { } bin) await Console.Out.WriteLineAsync($"  binary: {bin}");
        if (ServiceTxnMarker.Exists(store, id) && !ServiceTxnLock.IsHeld(store, id))
            await Console.Out.WriteLineAsync(UnfinishedTxnLines(
                id, ServiceTxnMarker.Read(store, id), ServiceTxnMarker.WrittenAt(store, id)));
        return 0;
    }

    /// <summary>The human status lines for a marker no running operation holds.</summary>
    internal static string UnfinishedTxnLines(string id, TxnMarker? marker, DateTimeOffset? writtenAt) {
        var what = marker is null ? "operation" : marker.Operation;
        var where = marker is null ? "" : $" at '{marker.Phase}'";
        var when = writtenAt is { } at ? $" ({at.ToLocalTime():yyyy-MM-dd HH:mm})" : "";
        return $"  An earlier service {what} did not finish: it stopped{where}{when}.\n"
             + $"  Repair it with: kcap daemon service install --replace --verify --name {id}";
    }

    async Task<int> StatusJson() {
        await StaleTxnMarker.SettleAsync(store, manager, id, time);
        var query           = manager.Query(id);
        var installBinary   = UnitIdentity.ResolveDaemonBinary();
        var daemonPid       = DaemonPidProbe.ValidatedPid(store, id);
        var txnActive       = ServiceTxnLock.IsHeld(store, id);
        var txnMarker       = ServiceTxnMarker.Exists(store, id);
        var marker          = txnMarker ? ServiceTxnMarker.Read(store, id) : null;
        var markedAt        = txnMarker ? ServiceTxnMarker.WrittenAt(store, id) : null;
        var (unitProfile, unitServerUrl, unitExpectedServer, unitConsentSeed) = UnitEnvEvidence(query.UnitPresent);

        string? supervisor = null;
        bool? upgrade = null, installable = null;
        if (OperatingSystem.IsWindows()) {
            supervisor  = ServiceManagerFactory.SupervisorWord(manager);
            upgrade     = manager is WindowsServiceManager ? WindowsServiceBinaries.UpgradeAvailable() : null;
            installable = manager is WindowsScheduledTaskServiceManager && WindowsServiceBinaries.Bundled() is not null;
        }

        var (json, exitCode) = ServiceStatusRender.Render(
            query, id, installBinary, daemonPid, txnMarker, txnActive,
            unitProfile, unitServerUrl, unitExpectedServer, unitConsentSeed,
            marker, markedAt, supervisor, upgrade, installable);

        if (json is null) {
            await Console.Error.WriteLineAsync($"Could not determine service status for '{id}' ({manager.Describe()}).");
            return exitCode;
        }

        await Console.Out.WriteLineAsync(json);
        return exitCode;
    }

    /// <summary>
    /// The flow's daemon-install ladder: from a fresh status read, install or start so the machine is
    /// reachable, baking the born-<c>prompt</c> consent-seed directive on install and gating the start
    /// exactly as an app-managed start is. A gate refusal exits with the verify transaction's coded
    /// exit plus one <c>start_gate_reason=</c> line, mapped machine-readably to
    /// <c>recovery_surface=takeover|reinstall|attention</c> (the pinned <see cref="ReasonRouting"/>
    /// table) — never guessed at from prose. On non-launchd the ladder degrades to plain install/start
    /// (no gates, no rollback); the JSON reports <c>verified:false</c> so the flow's copy can say so.
    /// On launchd a resolvable profile is required up front — a unit baked without one could never
    /// pass the start gate's identity half, so ensure refuses rather than report a dead-end install.
    /// </summary>
    internal async Task<int> Ensure(string[] args) {
        var json        = args.Contains("--json");
        // Same explicit-flag-wins-then-resolved default as Install: the gate's identity half needs a
        // non-empty KCAP_PROFILE, so a bare `ensure` on launchd must still carry the resolved name.
        var profileName = DaemonCommands.ExtractFlagValue(args, "--profile") ?? profiles.Resolution.ProfileName;

        var (result, exit) = await EvaluateEnsureAsync(profileName);

        return await Report(result, exit, json);
    }

    /// <summary>
    /// The ladder alone: what <see cref="Ensure"/> decided and did, with no console output of its own
    /// and no reporting.
    ///
    /// <para><b>The one place the operation is composed</b>, so the browser flow and the verb cannot end
    /// up running different ladders — the same split <c>DaemonShimCommands.EvaluateAsync</c> has, and for
    /// the same reason.</para>
    ///
    /// <para><b>Not silent, though.</b> The mutating arms run the verify engine, which writes its coded
    /// tokens to stderr; what this method drops is the verb's own reporting, not the transaction's
    /// diagnostics. A caller that owns the terminal has to stop competing for it first.</para>
    /// </summary>
    /// <param name="retireServiceId">Retired by the install arm's transaction; the start arms never retire.</param>
    internal async Task<(ServiceEnsureJson Result, int Exit)> EvaluateEnsureAsync(
            string? profileName, string? retireServiceId = null) {
        await StaleTxnMarker.SettleAsync(store, manager, id, time);
        var query     = manager.Query(id);
        var daemonPid = DaemonPidProbe.ValidatedPid(store, id);
        var txnActive = ServiceTxnLock.IsHeld(store, id);
        var txnMarker = ServiceTxnMarker.Exists(store, id);

        // The classifier gets the job pid plus whether THIS manager can supply one: on launchd,
        // already-enabled additionally requires the running job to own the validated daemon pid
        // (systemd/Windows cannot report one, so their coarser verified:false arm stands).
        var decision = EnsureClassifier.Classify(
            query.Probe, query.State, query.UnitPresent, daemonPid, txnMarker, txnActive,
            query.JobPid, manager is LaunchdServiceManager);

        var state = ServiceStateToken(query);

        // On launchd the start gate's identity half demands a non-empty invoking KCAP_PROFILE, so a
        // unit baked without one can never be gated-started — installing it bakes a dead end. Fail
        // closed with a coded reason rather than letting the install report success. (A URL-only
        // unit is only coherent off-macOS, where there is no gate; there the plain arms stand.)
        if (LaunchdProfileRefusal(manager is LaunchdServiceManager, profileName, decision.Action)) {
            var refusedAction = decision.Action == EnsureAction.Install ? "install" : "start";
            return (new ServiceEnsureJson(id, state, refusedAction, "refused", null, "no_profile_configured"), 1);
        }

        switch (decision.Action) {
            case EnsureAction.AlreadyEnabled:
                return (new ServiceEnsureJson(id, state, "none", "already_enabled"), 0);
            case EnsureAction.Attention:
                return (new ServiceEnsureJson(id, state, "none", "attention", null, decision.Reason), 1);
            case EnsureAction.Install:
                return await EnsureInstall(profileName, state, retireServiceId);
            case EnsureAction.Start:
                return await EnsureStart(profileName, state);
            default:
                // Unreachable: Classify is total over EnsureAction. Kept as a fail-closed tail so an
                // added enum member can never fall through to exit 0.
                return (new ServiceEnsureJson(id, state, "none", "attention", null, "status_unknown"), 1);
        }
    }

    /// <summary>
    /// The server URL the unit bakes as <c>KCAP_EXPECT_SERVER_URL</c>, or null when none resolves.
    /// An explicit <c>--profile P</c> resolves P's own URL (a flag naming a different profile than
    /// the active one must not bake the active profile's URL — the unit would refuse to boot);
    /// otherwise the resolved profile's.
    /// </summary>
    async Task<string?> ResolveServerUrlAsync(string? profileName) =>
        profileName is null
            ? profiles.Resolution.ServerUrl
            : await ResolveNamedProfileServerUrlAsync(profileName);

    async Task<string?> ResolveNamedProfileServerUrlAsync(string profileName) {
        var config = await AppConfig.LoadProfileConfig(root);
        return config.Profiles.TryGetValue(profileName, out var p) ? p.ServerUrl : null;
    }

    /// <summary>Wire token for the fresh-read state. An Unknown probe is reported as "unknown" rather
    /// than falling through to a state value — the same never-masquerade rule status --json applies.
    /// A unit that is present but stopped reads <c>NotInstalled</c> on launchd (the label is not
    /// loaded) yet is genuinely installed — derive from unit presence so the JSON never pairs
    /// <c>state:"not_installed"</c> with <c>action:"start"</c>.</summary>
    internal static string ServiceStateToken(ServiceQuery q) => q.Probe == LabelProbe.Unknown
        ? "unknown"
        : q.State switch {
            ServiceState.NotInstalled when q.UnitPresent => "installed",
            ServiceState.NotInstalled => "not_installed",
            ServiceState.Installed    => "installed",
            ServiceState.Running      => "running",
            _                         => "unknown",
        };

    /// <summary>Emit one ensure result: JSON on stdout when <c>--json</c>, a human line otherwise.
    /// The exit code is decided by the caller, never derived from the outcome string.
    ///
    /// <para>The two stderr lines are derived from the result rather than written where the row was
    /// decided, so the ladder itself reports nothing: <c>recovery_surface=</c> wherever a surface was
    /// mapped, and the install hint for the one refusal a user can act on directly.</para></summary>
    async Task<int> Report(ServiceEnsureJson result, int exit, bool json) {
        if (result.Reason == "daemon_not_found")
            await Console.Error.WriteLineAsync(DaemonCommands.DaemonNotFoundMessage());

        if (result.Recovery is { } surface)
            await Console.Error.WriteLineAsync($"recovery_surface={surface}");

        if (json) {
            await Console.Out.WriteLineAsync(ServiceEnsureRender.RenderJson(result));
            return exit;
        }

        var line = result.Outcome switch {
            "already_enabled" => $"Service '{id}' is already enabled.",
            "attention"       => $"Service '{id}': {result.Reason} — no changes made.",
            "installed"       => $"Service '{id}' installed ({(result.Verified ? "verified" : "plain")}).",
            "started"         => $"Service '{id}' started ({(result.Verified ? "verified" : "plain")}).",
            "refused"         => $"Service '{id}': {result.Reason}{(result.Recovery is { } r ? $" — {r} needed" : "")}.",
            _                 => $"Service '{id}': unexpected outcome '{result.Outcome}'.",
        };
        await Console.Out.WriteLineAsync(line);
        return exit;
    }

    /// <summary>Install arm of <see cref="Ensure"/>: force-bake the born-<c>prompt</c> directive (and the
    /// expected-server pin the identity half of the start gate re-reads), then run the verified
    /// transaction on launchd (or on Windows when retiring a renamed service) or a plain install
    /// elsewhere.</summary>
    async Task<(ServiceEnsureJson, int)> EnsureInstall(string? profileName, string state, string? retireServiceId) {
        var serverUrl = await ResolveServerUrlAsync(profileName);
        if (serverUrl is null)
            return (new ServiceEnsureJson(id, state, "install", "refused", null, "no_server_configured"), 1);

        var daemonPath = UnitIdentity.ResolveDaemonBinary();
        if (daemonPath is null)
            return (new ServiceEnsureJson(id, state, "install", "refused", null, "daemon_not_found"), 1);

        // The app's MutationEnv overlay, in-process: seed born-prompt + the expected server the gate
        // and the daemon's own boot both re-read. Everything else comes from the ambient capture.
        var env = EnsureUnitEnv(profileName, serverUrl, ServiceEnvironment.Capture(profileName, root, home));
        env["KCAP_DAEMON_SUPERVISED"] = id;
        var logPath = root.Path($"daemon-{id}.log");
        var spec    = new ServiceSpec(id, daemonPath, logPath, env, []);

        StartGateReason? gateReason = null;
        string? viabilityReason = null;
        string? bootRefusalToken = null;
        var verified = manager is LaunchdServiceManager;
        int exit;
        if (manager is LaunchdServiceManager) {
            var profileUrlValid = await ServiceInstallViability.PinnedProfileServerUrlValidAsync(env, root);
            var engine = new ServiceVerify(store, root, (LaunchdServiceManager)manager,
                n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, time, t),
                time, profileViable: () => profileUrlValid, gateEnv: EnsureGateEnv(profileName, serverUrl));
            exit = await engine.InstallVerifiedAsync(spec, replace: false, CapacitorVersion.Current(), retireServiceId);
            // InstallVerifiedAsync never returns StartGate — its gated refusals are viability/drift —
            // so gateReason stays null here and StartGate recovery never fires from the install arm.
            gateReason       = engine.LastGateReason;
            viabilityReason  = engine.LastViabilityReason;
            bootRefusalToken = engine.LastBootRefusalToken;
        } else if (retireServiceId is not null) {
            // Only the verified install retires, so a rename takes it over the plain one.
            var profileUrlValid = await ServiceInstallViability.PinnedProfileServerUrlValidAsync(env, root);
            exit = await NewPollingVerify(() => profileUrlValid)
                .InstallVerifiedAsync(spec, replace: false, CapacitorVersion.Current(), retireServiceId);
            verified = true;
        } else {
            exit = await InstallPlain(spec, startNow: true);
        }

        if (exit != 0) return EnsureFailure(exit, gateReason, state, "install", viabilityReason, bootRefusalToken, verified);
        return (new ServiceEnsureJson(id, state, "install", "installed", Verified: verified), 0);
    }

    /// <summary>Pure: whether a launchd ensure run must refuse for want of a profile name. The start
    /// gate's identity half demands a non-empty invoking <c>KCAP_PROFILE</c>, so a unit baked without
    /// one can never be gated-started — installing it would bake a dead end. Only launchd gates at
    /// all, so URL-only installs stay coherent off-macOS (no gate); the predicate takes
    /// <c>isLaunchd</c> rather than the manager so it is testable without a real launchd manager.</summary>
    internal static bool LaunchdProfileRefusal(bool isLaunchd, string? profileName, EnsureAction action) =>
        isLaunchd && string.IsNullOrEmpty(profileName) && action is EnsureAction.Install or EnsureAction.Start;

    /// <summary>Pure: the unit env for an ensure install — the ambient capture, overlaid with the
    /// born-<c>prompt</c> directive and the expected-server pin, so both are deliberate unit content
    /// regardless of what the installing shell exported. <c>prompt</c> WINS over an ambient value
    /// (a refusal exported as <c>deny</c>/<c>allow</c> must not survive into the unit): the flow's
    /// contract is "an app-installed daemon is born prompt", and the gate's identity half re-reads
    /// the expect pin, so a unit without it can never pass a later gated start.
    /// <para>When a profile IS pinned, the ambient <c>KCAP_URL</c> is stripped: URL resolution
    /// outranks profile resolution (ProfileResolver), so a unit that bakes both would resolve the
    /// ambient URL and refuse on the expectation mismatch — the pin must be the sole URL authority.</para></summary>
    internal static Dictionary<string, string> EnsureUnitEnv(
            string? profileName, string serverUrl, IReadOnlyDictionary<string, string> ambient) {
        var env = new Dictionary<string, string>(ambient) {
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_EXPECT_SERVER_URL"]    = serverUrl,
        };
        // Same "explicit pin wins" rule as ServiceEnvironment.Build — only a real profile is pinned.
        if (!string.IsNullOrEmpty(profileName)) {
            env[ProfileOverrides.ProfileVar] = profileName;
            env.Remove(ProfileOverrides.UrlVar);
        }
        return env;
    }

    /// <summary>Start arm of <see cref="Ensure"/>: the gated, app-managed start on launchd (the gate
    /// fires because <see cref="EnsureGateEnv"/> carries the directive), plain elsewhere.</summary>
    async Task<(ServiceEnsureJson, int)> EnsureStart(string? profileName, string state) {
        var serverUrl = await ResolveServerUrlAsync(profileName);
        if (serverUrl is null)
            return (new ServiceEnsureJson(id, state, "start", "refused", null, "no_server_configured"), 1);

        StartGateReason? gateReason = null;
        string? bootRefusalToken = null;
        int exit;
        if (manager is LaunchdServiceManager) {
            var engine = new ServiceVerify(store, root, (LaunchdServiceManager)manager,
                n => DaemonPidProbe.ValidatedPid(store, n), (n, t) => HelloProbe.RunAsync(store, n, time, t),
                time, gateEnv: EnsureGateEnv(profileName, serverUrl));
            exit = await engine.StartVerifiedAsync(id);
            gateReason       = engine.LastGateReason;
            bootRefusalToken = engine.LastBootRefusalToken;
        } else {
            exit = await StartPlain();
        }

        if (exit != 0) return EnsureFailure(exit, gateReason, state, "start", bootRefusalToken: bootRefusalToken);
        return (new ServiceEnsureJson(id, state, "start", "started", Verified: manager is LaunchdServiceManager), 0);
    }

    /// <summary>Gate env for ensure's in-process engine: the directive the app's MutationEnv would
    /// overlay on a child, plus the profile/expect the identity half re-reads. Falls through to the
    /// process env for anything else, so an operator's own KCAP_* exports still apply.</summary>
    static Func<string, string?> EnsureGateEnv(string? profileName, string? serverUrl) => k => k switch {
        "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
        ProfileOverrides.ProfileVar => profileName,
        "KCAP_EXPECT_SERVER_URL"    => serverUrl,
        _                           => Environment.GetEnvironmentVariable(k),
    };

    /// <summary>Shared non-success tail: the verify transaction already emitted its coded token and
    /// <c>start_gate_reason=</c> line; the pure <see cref="EnsureFailureMap"/> derives the JSON's
    /// recovery/reason fields — including the engine's structured viability/boot-refusal reasons —
    /// and the <c>recovery_surface=</c> line is re-emitted when a surface is mapped. The verified
    /// flag matches this run's transaction (true on the launchd arms), and the exit code always
    /// passes through unchanged.</summary>
    (ServiceEnsureJson, int) EnsureFailure(int exit, StartGateReason? gateReason, string state, string action,
            string? viabilityReason = null, string? bootRefusalToken = null, bool? verifiedRun = null) {
        var verified = verifiedRun ?? manager is LaunchdServiceManager;
        var (recovery, reason) = EnsureFailureMap.Map(exit, gateReason, verified, viabilityReason, bootRefusalToken);
        return (new ServiceEnsureJson(id, state, action, "refused", recovery, reason, Verified: verified), exit);
    }

    /// <summary>
    /// UX-evidence-only re-read of the installed unit's baked environment (spec §3): which profile,
    /// server URL, expectation and consent-seed default it was installed with. Sourced by re-reading the
    /// plist rather than threading it through <see cref="ServiceQuery"/> so the query type stays a pure
    /// lifecycle probe. All four are null when no unit is present or the re-read/parse fails for any
    /// reason (moved/corrupt plist, permissions) — this is evidence for an operator, never load-bearing.
    /// </summary>
    (string? Profile, string? ServerUrl, string? ExpectedServer, string? ConsentSeed)
            UnitEnvEvidence(bool unitPresent) {
        if (!unitPresent) return (null, null, null, null);

        try {
            if (manager.UnitEnvironment(id) is not { } env) return (null, null, null, null);
            env.TryGetValue(ProfileOverrides.ProfileVar, out var profile);
            env.TryGetValue("KCAP_EXPECT_SERVER_URL", out var expectedServer);
            env.TryGetValue("KCAP_CONSENT_SEED_DEFAULT", out var consentSeed);

            var serverUrl = env.TryGetValue(ProfileOverrides.UrlVar, out var bakedUrl)
                ? bakedUrl
                : BakedProfileServerUrl(env, profile, root);

            return (profile, serverUrl, expectedServer, consentSeed);
        } catch {
            return (null, null, null, null);
        }
    }

    /// <summary>The <c>KCAP_URL</c>-absent fallback for <see cref="UnitEnvEvidence"/>: the baked
    /// profile's <c>server_url</c>, read from the baked <c>KCAP_CONFIG_DIR</c> (or the default config
    /// root when none was baked) — null on any ambiguity (no baked profile) or miss.</summary>
    static string? BakedProfileServerUrl(IReadOnlyDictionary<string, string> env, string? profile, ConfigRoot root) {
        if (string.IsNullOrEmpty(profile)) return null;
        try {
            var config = ConfigMutator.LoadPure(UnitIdentity.ConfigPathFromUnitEnv(env, root));
            return config.Profiles.TryGetValue(profile, out var p) ? p.ServerUrl : null;
        } catch {
            return null;
        }
    }

    static int Usage() {
        Console.Error.WriteLine("Usage: kcap daemon service <install|uninstall|start|stop|ensure|status|refresh> [--name N]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  install [--name N] [--profile P] [--max-agents N] [--no-start] [--replace] [--verify] [--retire ID]");
        Console.Error.WriteLine("                          --verify polls readiness/version/ownership and rolls back on failure");
        Console.Error.WriteLine("                          --replace (requires --verify) takes over an existing label/unit/live owner");
        Console.Error.WriteLine("                          --retire ID (requires --replace --verify) also removes unit ID in the same transaction");
        Console.Error.WriteLine("                          --no-start is incompatible with --verify");
        Console.Error.WriteLine("  install --system [...] Windows: install or update kcap-service (one administrator prompt), then hand the daemon to it");
        Console.Error.WriteLine("  uninstall [--name N]   Stop and remove the service unit");
        Console.Error.WriteLine("  uninstall --system     Windows: hand the daemon back, then remove kcap-service (one administrator prompt)");
        Console.Error.WriteLine("  uninstall --under DIR  Remove every unit whose daemon binary is inside DIR");
        Console.Error.WriteLine("  start [--name N] [--verify]   Start the installed service now");
        Console.Error.WriteLine("                          --verify polls readiness/ownership and rolls back on failure");
        Console.Error.WriteLine("  stop [--name N] [--keep-agents]   Stop the running service (stays installed) and end its agents;");
        Console.Error.WriteLine("                          --keep-agents keeps them for the next start to resume");
        Console.Error.WriteLine("  ensure [--name N] [--profile P] [--json]   Install-or-start from a fresh status read");
        Console.Error.WriteLine("                          (bakes the born-prompt consent seed; gate refusals emit recovery_surface=)");
        Console.Error.WriteLine("  status [--name N] [--json]   Show installed/running state (--json for machine-readable output)");
        Console.Error.WriteLine("  refresh                Update installed units to this version's format; a reload resumes the daemon's agents");
        return 1;
    }
}

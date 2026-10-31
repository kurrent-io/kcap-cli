using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Services;

/// <summary>
/// The <c>--verify</c> contract for a supervisor whose <see cref="IServiceManager.Query"/> reports the
/// job's own pid (a Windows Scheduled Task, a systemd user unit): mutate, then prove the service's own
/// daemon answers a hello under the right name, protocol and version before reporting success, and undo
/// the mutation when it does not. Exit codes and tokens are <see cref="VerifyExit"/>'s, so the desktop
/// app classifies the outcome exactly as a launchd one.
///
/// <para>Smaller than the launchd engine because neither supervisor keeps a loaded-but-stale definition
/// to boot out: the registration is replaced outright, and stopping ends the daemon with it.</para>
/// </summary>
sealed class PollingServiceVerify(
        DaemonStore store,
        IServiceManager manager,
        Func<string, int?> validatedDaemonPid,
        Func<string, TimeSpan, Task<HelloProbeResult>> hello,
        TimeProvider time,
        Func<bool>? profileViable = null,
        TimeSpan? forwardBudget = null,
        Func<string, CancellationToken, Task<AdmissionFenceAcquireResult>>? acquireFence = null,
        Func<string, TimeSpan, Task>? endDaemon = null,
        bool endsManualOwner = false) {
    static readonly TimeSpan LockWait     = TimeSpan.FromSeconds(10);
    static readonly TimeSpan FenceWait    = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan KillWait     = TimeSpan.FromSeconds(5);

    readonly TimeSpan _forwardBudget = forwardBudget ?? ServiceVerify.DefaultForwardBudget;

    /// <summary>Asks a daemon to end its agents and exit, within the wait it is given</summary>
    readonly Func<string, TimeSpan, Task> _endDaemon = endDaemon ?? (async (name, wait) =>
        await Commands.DaemonShutdownClient.StopAndWaitAsync(store, name, new ShutdownRequestDto("end", "final"), wait, time, CancellationToken.None));

    readonly Func<string, CancellationToken, Task<AdmissionFenceAcquireResult>> _acquireFence =
        acquireFence ?? ((name, ct) => AdmissionFenceClient.AcquireAsync(store, name, FenceWait, time, ct));

    public async Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion, string? retireServiceId = null) {
        var id = spec.ServiceId;
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, LockWait, time);
        if (txn is null) return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        if (profileViable is not null && !profileViable()) return Fail(VerifyExit.Viability, VerifyExit.ViabilityToken);

        IAdmissionFenceSession? fence = null;
        if (retireServiceId is not null) {
            var (refused, present, session) = await PrepareRetireAsync(retireServiceId, spec);
            if (refused is { } exit) return exit;
            fence = session;
            if (!present) retireServiceId = null;
        }

        try {
            return await InstallAfterRetirePreparedAsync(spec, replace, expectedVersion, retireServiceId, fence);
        } finally {
            if (fence is not null) await fence.DisposeAsync();
        }
    }

    async Task<int> InstallAfterRetirePreparedAsync(
            ServiceSpec spec, bool replace, string? expectedVersion, string? retireServiceId, IAdmissionFenceSession? fence) {
        var id = spec.ServiceId;

        // A rename never takes over a daemon that already answers to the new name.
        if (retireServiceId is not null && validatedDaemonPid(id) is not null)
            return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        var pre = manager.Query(id);
        if (pre.Probe == LabelProbe.Unknown) return Unanswered(id);
        if (!replace && (pre.Probe == LabelProbe.Loaded || pre.UnitPresent))
            return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        if (replace && pre.Probe == LabelProbe.Loaded && !await StopConfirmedAsync(id))
            return Fail(VerifyExit.StopUnconfirmed, VerifyExit.StopUnconfirmedToken);

        // A daemon started by hand holds the name with no unit to stop it: left running, the service's own
        // daemon would only be refused the name and the install would time out.
        if (replace && endsManualOwner && pre.Probe != LabelProbe.Loaded && validatedDaemonPid(id) is not null && !await EndManualOwnerAsync(id))
            return Fail(VerifyExit.StopUnconfirmed, VerifyExit.StopUnconfirmedToken);

        string? installError;
        try {
            manager.Install(spec, startNow: true, out installError);
        } catch (Exception ex) {
            installError = ex.Message;
        }

        if (installError is not null) {
            Say($"install: {installError}");
            return RollBackInstall(id, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
        }

        if (!await WaitReadyAsync(id, expectedVersion))
            return RollBackInstall(id, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);

        if (retireServiceId is null) return VerifyExit.Ok;

        // A manager whose install ended the old daemon has nothing left to fence; under any other, a daemon that
        // is merely absent may be its supervisor's relaunch on the way.
        if (fence is not null && !await fence.CommitAsync(FenceWait, CancellationToken.None)
            && !(manager.InstallEndsReplacedDaemon && validatedDaemonPid(retireServiceId) is null)) {
            await fence.AbortAsync(FenceWait, CancellationToken.None);
            return RetireRefusal("fence_unavailable");
        }

        if (!manager.Uninstall(retireServiceId, out var retireError)) {
            Say($"retire: {retireError}");
            // The old daemon keeps running, so it must not stay fenced; its abort deletes the marker.
            if (fence is not null) await fence.AbortAsync(FenceWait, CancellationToken.None);
            return RetireRefusal("uninstall_failed");
        }

        // Left behind, the marker would fence the next daemon started under the old name.
        if (fence is not null && File.Exists(store.RetiringMarkerPath(retireServiceId))) {
            try { File.Delete(store.RetiringMarkerPath(retireServiceId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Say($"retire: could not remove the retiring marker: {ex.Message}"); }
        }

        return VerifyExit.Ok;
    }

    /// <summary>
    /// Proves the task a rename leaves behind is this profile's and, when its daemon runs, takes that
    /// daemon's admission fence, before anything changes. Uninstalling the task kills the daemon, so one
    /// that is busy, or cannot be fenced, is refused; so is an installed task whose wrapper cannot be read.
    /// </summary>
    async Task<(int? Exit, bool Present, IAdmissionFenceSession? Fence)> PrepareRetireAsync(string retireId, ServiceSpec spec) {
        ServiceQuery old;
        try {
            old = manager.Query(retireId);
        } catch (InvalidOperationException ex) {
            Say($"retire: {ex.Message}");
            return (RetireRefusal("unit_unknown"), true, null);
        }

        // Its state says nothing when the supervisor did not answer, so it can be neither fenced nor called stopped.
        if (old.Probe == LabelProbe.Unknown) return (RetireRefusal("unit_unknown"), true, null);
        if (old.Probe != LabelProbe.Loaded && !old.UnitPresent) return (null, false, null);

        IReadOnlyDictionary<string, string>? retiredEnv;
        try {
            retiredEnv = manager.UnitEnvironment(retireId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            Say($"retire: {ex.Message}");
            retiredEnv = null;
        }
        if (retiredEnv is null) return (RetireRefusal("unit_unreadable"), true, null);
        if (!SameProfile(retiredEnv, spec.Environment)) return (RetireRefusal("foreign_profile"), true, null);

        if (old.State != ServiceState.Running) return (null, true, null);
        if (old.JobPid is not { } jobPid) return (RetireRefusal("fence_unavailable"), true, null);

        var acquired = await _acquireFence(retireId, CancellationToken.None);
        switch (acquired.Outcome) {
            case AdmissionFenceOutcome.Busy:        return (RetireRefusal("agents_active"), true, null);
            case AdmissionFenceOutcome.Unsupported: return (RetireRefusal("fence_unsupported"), true, null);
            case AdmissionFenceOutcome.Acquired:
                // The socket must answer for the process the uninstall will kill.
                if (acquired.Session!.Pid == jobPid) return (null, true, acquired.Session);
                await acquired.Session.DisposeAsync();
                return (RetireRefusal("fence_unavailable"), true, null);
            default:
                return (RetireRefusal("fence_unavailable"), true, null);
        }
    }

    public async Task<int> StartVerifiedAsync(string id) {
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, LockWait, time);
        if (txn is null) return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        var probe = manager.Query(id).Probe;
        if (probe == LabelProbe.Unknown) return Unanswered(id);
        if (probe != LabelProbe.Loaded) {
            Say($"Service '{id}' is not installed.");
            return 1;
        }

        if (await IsReadyAsync(id, expectedVersion: null)) return VerifyExit.Ok;

        if (!manager.Start(id, out var error)) {
            Say($"start: {error}");
            return Fail(VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
        }

        if (await WaitReadyAsync(id, expectedVersion: null)) return VerifyExit.Ok;

        manager.Stop(id, out _);
        return Fail(VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
    }

    async Task<bool> WaitReadyAsync(string id, string? expectedVersion) {
        var deadline = time.GetUtcNow() + _forwardBudget;
        while (true) {
            if (await IsReadyAsync(id, expectedVersion)) return true;
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }
    }

    // Hello and the pid are read separately, so one reading can join a hello from one daemon to the pid
    // of its replacement. As the launchd engine does, a second reading must name the same pid.
    async Task<bool> IsReadyAsync(string id, string? expectedVersion) =>
        await ReadyPidAsync(id, expectedVersion) is { } pid && await ReadyPidAsync(id, expectedVersion) == pid;

    // The same four legs the launchd engine requires: a well-formed hello, from this name, on the
    // current protocol and expected version, and from the daemon the task itself runs.
    async Task<int?> ReadyPidAsync(string id, string? expectedVersion) {
        var h = await hello(id, PollInterval * 4);
        if (!h.WellFormed || h.DaemonName != id || h.ProtocolVersion != HelloProtocol.CurrentVersion) return null;
        if (expectedVersion is not null && h.DaemonVersion != expectedVersion) return null;

        var jobPid = manager.Query(id).JobPid;
        return jobPid is not null && jobPid == validatedDaemonPid(id) ? jobPid : null;
    }

    async Task<bool> StopConfirmedAsync(string id) {
        if (!manager.Stop(id, out var error)) Say($"stop: {error}");
        var deadline = time.GetUtcNow() + KillWait;
        while (validatedDaemonPid(id) is not null) {
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }
        return true;
    }

    async Task<bool> EndManualOwnerAsync(string id) {
        await _endDaemon(id, KillWait);
        var deadline = time.GetUtcNow() + KillWait;
        while (validatedDaemonPid(id) is not null) {
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }
        return true;
    }

    int RollBackInstall(string id, int reasonExit, string reasonToken) {
        if (!manager.Uninstall(id, out var error)) {
            Say($"uninstall: {error}");
            return Fail(VerifyExit.RestoreVerification, VerifyExit.RestoreVerificationToken);
        }
        return Fail(reasonExit, reasonToken);
    }

    static bool SameProfile(IReadOnlyDictionary<string, string> retired, IReadOnlyDictionary<string, string> installing) =>
        retired.TryGetValue(Core.Config.ProfileOverrides.ProfileVar, out var a) && !string.IsNullOrEmpty(a)
     && installing.TryGetValue(Core.Config.ProfileOverrides.ProfileVar, out var b) && string.Equals(a, b, StringComparison.Ordinal);

    static int RetireRefusal(string reason) {
        Say($"retire_reason={reason}");
        return Fail(VerifyExit.RetireRefused, VerifyExit.RetireRefusedToken);
    }

    // Nothing is known about the unit, so neither installing over it nor starting it is safe to attempt.
    int Unanswered(string id) {
        Say($"query: {manager.Describe()} did not report the state of '{id}'");
        return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);
    }

    static int Fail(int exit, string token) {
        Say(token);
        return exit;
    }

    // Closed-stdio tolerant, like the launchd engine: the app reads these lines, and a broken pipe on
    // one must not turn a settled outcome into a crash.
    static void Say(string line) {
        try { Console.Error.WriteLine(line); } catch (IOException) { }
    }
}

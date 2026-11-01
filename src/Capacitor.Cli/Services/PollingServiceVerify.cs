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
        bool endsManualOwner = false,
        TimeSpan? bootBudget = null,
        Func<string, string?>? endNotePath = null) {
    static readonly TimeSpan LockWait     = TimeSpan.FromSeconds(10);
    static readonly TimeSpan FenceWait    = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan KillWait     = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long readiness keeps waiting, in all, for a daemon its unit is still running but that has not
    /// answered yet. The daemon probes every vendor CLI before it binds its socket, which on a cold Windows
    /// profile outlasts the forward budget; ending it then kills a boot that was going to succeed. The
    /// desktop app's timeout for a verified install or start must sit above this plus the lock wait and
    /// rollback.
    /// </summary>
    public static readonly TimeSpan DefaultBootBudget = TimeSpan.FromSeconds(90);

    readonly TimeSpan _forwardBudget = forwardBudget ?? ServiceVerify.DefaultForwardBudget;
    readonly TimeSpan _bootBudget = bootBudget ?? TimeSpan.Zero;

    // The leg the last failed readiness reading stopped at, and how long readiness waited in all.
    string _notReadyLeg = ReadinessLeg.NoAnswer;
    TimeSpan _waited;

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

        if (!await WaitReadyAsync(id, expectedVersion)) {
            var exit = RollBackInstall(id, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
            if (exit == VerifyExit.ReadinessTimeout) NoteEnded(id, "install");
            return exit;
        }

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

        if (manager.Stop(id, out _)) NoteEnded(id, "start");
        return Fail(VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
    }

    async Task<bool> WaitReadyAsync(string id, string? expectedVersion) {
        var started = time.GetUtcNow();
        while (true) {
            if (await IsReadyAsync(id, expectedVersion)) return true;
            _waited = time.GetUtcNow() - started;
            if (_waited >= _forwardBudget && (_waited >= _bootBudget || !StillStarting(id))) {
                Say($"readiness_leg={_notReadyLeg}");
                return false;
            }
            await Task.Delay(PollInterval, time, CancellationToken.None);
        }
    }

    // Nothing has answered while the unit still runs: the daemon is booting, or its supervisor is
    // relaunching it. A daemon that answers wrongly is up, so it gets no extra time.
    bool StillStarting(string id) {
        if (_notReadyLeg != ReadinessLeg.NoAnswer) return false;
        try {
            return manager.Query(id).State == ServiceState.Running;
        } catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    // Hello and the pid are read separately, so one reading can join a hello from one daemon to the pid
    // of its replacement. As the launchd engine does, a second reading must name the same pid.
    async Task<bool> IsReadyAsync(string id, string? expectedVersion) {
        if (await ReadyPidAsync(id, expectedVersion) is not { } pid) return false;
        var again = await ReadyPidAsync(id, expectedVersion);
        if (again == pid) return true;
        if (again is not null) _notReadyLeg = ReadinessLeg.NotTaskOwned;
        return false;
    }

    // The same four legs the launchd engine requires: a well-formed hello, from this name, on the
    // current protocol and expected version, and from the daemon the task itself runs.
    async Task<int?> ReadyPidAsync(string id, string? expectedVersion) {
        var h = await hello(id, PollInterval * 4);
        if (!h.WellFormed) return NotReady(ReadinessLeg.NoAnswer);
        if (h.DaemonName != id || h.ProtocolVersion != HelloProtocol.CurrentVersion) return NotReady(ReadinessLeg.WrongDaemon);
        if (expectedVersion is not null && h.DaemonVersion != expectedVersion) return NotReady(ReadinessLeg.WrongVersion);

        var jobPid = manager.Query(id).JobPid;
        return jobPid is not null && jobPid == validatedDaemonPid(id) ? jobPid : NotReady(ReadinessLeg.NotTaskOwned);
    }

    int? NotReady(string leg) {
        _notReadyLeg = leg;
        return null;
    }

    /// <summary>
    /// Appends, beside the daemon's own output, why this verify ended it. Ended from outside, the daemon
    /// writes nothing more, so without this line its log stops mid-boot with no reason.
    /// </summary>
    void NoteEnded(string id, string verb) {
        if (endNotePath?.Invoke(id) is not { } path) return;
        var line = $"{time.GetLocalNow().DateTime:yyyy-MM-dd HH:mm:ss.fff} [verify] `kcap daemon service {verb} --verify` "
                 + $"ended this daemon after {(int)_waited.TotalSeconds}s: {ReadinessLeg.Describe(_notReadyLeg)}{Environment.NewLine}";
        try {
            File.AppendAllText(path, line);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Say($"could not record why the daemon was ended in {path}: {ex.Message}");
        }
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

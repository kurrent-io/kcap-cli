using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Services;

/// <summary>
/// The <c>--verify</c> contract on a Windows Scheduled Task: mutate, then prove the service's own daemon
/// answers a hello under the right name, protocol and version before reporting success, and undo the
/// mutation when it does not. Exit codes and tokens are <see cref="VerifyExit"/>'s, so the desktop app
/// classifies a Windows outcome exactly as a launchd one.
///
/// <para>Smaller than the launchd engine because a task has no loaded-but-stale definition to boot
/// out: <c>schtasks /Create /F</c> replaces the registration outright, and stopping ends the wrapper
/// and the daemon together.</para>
/// </summary>
sealed class WindowsServiceVerify(
        DaemonStore store,
        IServiceManager manager,
        Func<string, int?> validatedDaemonPid,
        Func<string, TimeSpan, Task<HelloProbeResult>> hello,
        TimeProvider time,
        Func<bool>? profileViable = null,
        TimeSpan? forwardBudget = null,
        Func<string, IReadOnlyDictionary<string, string>?>? unitEnv = null) {
    static readonly TimeSpan LockWait     = TimeSpan.FromSeconds(10);
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan KillWait     = TimeSpan.FromSeconds(5);

    readonly TimeSpan _forwardBudget = forwardBudget ?? ServiceVerify.DefaultForwardBudget;

    public async Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion, string? retireServiceId = null) {
        var id = spec.ServiceId;
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, LockWait, time);
        if (txn is null) return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        if (profileViable is not null && !profileViable()) return Fail(VerifyExit.Viability, VerifyExit.ViabilityToken);

        // A rename retires only a unit pinned to the same profile, checked before anything changes; an
        // absent unit has nothing to retire.
        if (retireServiceId is not null && unitEnv?.Invoke(retireServiceId) is { } retiredEnv
         && !SameProfile(retiredEnv, spec.Environment))
            return Fail(VerifyExit.RetireRefused, VerifyExit.RetireRefusedToken);

        // A rename never takes over a daemon that already answers to the new name.
        if (retireServiceId is not null && validatedDaemonPid(id) is not null)
            return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        var pre = manager.Query(id);
        if (!replace && (pre.Probe == LabelProbe.Loaded || pre.UnitPresent))
            return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        if (replace && pre.Probe == LabelProbe.Loaded && !await StopConfirmedAsync(id))
            return Fail(VerifyExit.StopUnconfirmed, VerifyExit.StopUnconfirmedToken);

        try {
            manager.Install(spec, startNow: true);
        } catch (Exception ex) {
            Say($"install: {ex.Message}");
            return RollBackInstall(id, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);
        }

        if (!await WaitReadyAsync(id, expectedVersion))
            return RollBackInstall(id, VerifyExit.ReadinessTimeout, VerifyExit.ReadinessTimeoutToken);

        if (retireServiceId is not null && !manager.Uninstall(retireServiceId, out var retireError)) {
            Say($"retire: {retireError}");
            return Fail(VerifyExit.RetireRefused, VerifyExit.RetireRefusedToken);
        }

        return VerifyExit.Ok;
    }

    public async Task<int> StartVerifiedAsync(string id) {
        using var txn = await ServiceTxnLock.TryAcquireAsync(store, id, LockWait, time);
        if (txn is null) return Fail(VerifyExit.Contended, VerifyExit.ContendedToken);

        if (manager.Query(id).Probe != LabelProbe.Loaded) {
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

    // The same four legs the launchd engine requires: a well-formed hello, from this name, on the
    // current protocol and expected version, and from the daemon the task itself runs.
    async Task<bool> IsReadyAsync(string id, string? expectedVersion) {
        var h = await hello(id, PollInterval * 4);
        if (!h.WellFormed || h.DaemonName != id || h.ProtocolVersion != HelloProtocol.CurrentVersion) return false;
        if (expectedVersion is not null && h.DaemonVersion != expectedVersion) return false;

        var jobPid = manager.Query(id).JobPid;
        return jobPid is not null && jobPid == validatedDaemonPid(id);
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

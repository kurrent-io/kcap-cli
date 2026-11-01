using System.Runtime.Versioning;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Moves this user's local server to <c>kcap-service</c> when the service is installed, and back to a <c>kcap-local</c>
/// task before it is removed. Each move holds the lifecycle lease and the unit lock, keeps a stopped server stopped,
/// and ends with exactly one supervisor: one that cannot finish puts the task back.
/// </summary>
static class LocalServerMigration {
    static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    /// <summary>A service installed a moment ago may not have opened its pipe yet.</summary>
    static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(10);

    [SupportedOSPlatform("windows")]
    public static Task<string?> ToServiceAsync(LocalRoot root, UserHome home, TimeProvider time) {
        var pipe        = new WindowsServicePipeClient(time);
        var enrollments = new RegistryLocalServerEnrollmentStore();
        var task        = new LocalServerTaskService(home, time);

        return ToServiceAsync(root, home, time, task, new LocalServerWindowsService(home, time, pipe, enrollments, task), enrollments,
            () => ServicePipeVerbs.ServesLocalServer(pipe, ServiceWait), CancellationToken.None);
    }

    [SupportedOSPlatform("windows")]
    public static Task<string?> ToTaskAsync(UserHome home, TimeProvider time) {
        var enrollments = new RegistryLocalServerEnrollmentStore();
        var task        = new LocalServerTaskService(home, time);

        return ToTaskAsync(home, time, task, new LocalServerWindowsService(home, time, new WindowsServicePipeClient(time), enrollments, task),
            enrollments, CancellationToken.None);
    }

    /// <summary>Best effort: never throws, and leaves a server it could not move under its task.</summary>
    /// <returns>What to tell the user, or null when there was nothing to move.</returns>
    public static async Task<string?> ToServiceAsync(LocalRoot root, UserHome home, TimeProvider time, ILocalServerService task,
        LocalServerWindowsService service, ILocalServerEnrollmentStore enrollments, Func<bool> serviceRunsServers, CancellationToken ct) {
        if (enrollments.Read() is not null || Installed(root) is not { } installation) return null;

        var unit = LocalServerUnit.ForInstallation(root, installation.Port);
        if (!File.Exists(task.UnitFile(unit))) return null;

        if (!serviceRunsServers())
            return "kcap-service cannot run the local server, so it stays a Scheduled Task.";

        var running = false;

        try {
            using var lease = await LifecycleLease.AcquireAsync(root, LockWait, time, ct);
            if (lease.ReadJournal() is StateRead<LifecycleJournalEntry>.Present(var entry))
                return $"The local server stays a Scheduled Task: an interrupted {entry.Operation} is pending, which kcap local start finishes.";

            using var _ = await LocalUnitLock.AcquireAsync(home, LockWait, time, ct);

            if (LocalRootIdentity.Decide(root, task.Owner(unit)) != OwnerDecision.Ours)
                return $"The Scheduled Task {task.Name(unit)} is not provably this installation's, so the local server stays under it.";

            running = task.Activity(unit) is UnitActivity.Running or UnitActivity.Restarting;
            service.Install(unit, start: running);

            return enrollments.Read() is not null ? "Moved the local server from its Scheduled Task to kcap-service." : null;
        } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException or InvalidOperationException) {
            return $"The local server could not be moved to kcap-service ({ex.Message}).{await RestoreTaskAsync(unit, running, task, enrollments)}";
        }
    }

    static async Task<string> RestoreTaskAsync(LocalServerUnit unit, bool running, ILocalServerService task, ILocalServerEnrollmentStore enrollments) {
        if (enrollments.Read() is not null || task.IsInstalled(unit)) return "";

        try {
            task.Install(unit);
            if (!running) await task.StopAsync(unit, CancellationToken.None);

            return " Its Scheduled Task runs it again.";
        } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException) {
            return $" Putting its Scheduled Task back failed too ({ex.Message}); kcap local start installs it again.";
        }
    }

    /// <summary>Throws <see cref="LocalLifecycleException"/> when the server cannot be moved; the service must then stay,
    /// or nothing would supervise it.</summary>
    /// <returns>What to tell the user, or null when nothing was enrolled.</returns>
    public static async Task<string?> ToTaskAsync(UserHome home, TimeProvider time, ILocalServerService task, LocalServerWindowsService service,
        ILocalServerEnrollmentStore enrollments, CancellationToken ct) {
        if (enrollments.Read() is not { } enrollment) return null;

        var root = new LocalRoot(enrollment.Root, home);

        if (Installed(root) is not { } installation) {
            await service.UninstallAsync(LocalServerUnit.ForInstallation(root, 0), ct);
            return "Removed kcap-service's enrollment of a local server that is no longer installed.";
        }

        var unit = LocalServerUnit.ForInstallation(root, installation.Port);

        using var lease = await LifecycleLease.AcquireAsync(root, LockWait, time, ct);
        if (lease.ReadJournal() is StateRead<LifecycleJournalEntry>.Present(var entry))
            throw new LocalLifecycleException($"an interrupted {entry.Operation} of the local installation is pending; run kcap local start to finish it first");

        using var _ = await LocalUnitLock.AcquireAsync(home, LockWait, time, ct);

        try {
            await service.UninstallAsync(unit, ct);
        } catch (LocalLifecycleException) {
            // The service is staying, and its server must not stay stopped for a move that did not happen.
            if (enrollments.Read() is not null) enrollments.SetStopped(enrollment.Stopped);
            throw;
        }

        try {
            task.Install(unit);
            if (enrollment.Stopped) await task.StopAsync(unit, ct);
        } catch (Exception ex) when (ex is LocalLifecycleException or IOException or UnauthorizedAccessException) {
            throw new LocalLifecycleException(
                $"the local server was stopped and taken off kcap-service, but its Scheduled Task could not be installed ({ex.Message}); kcap local start installs it");
        }

        return enrollment.Stopped
            ? "Moved the local server from kcap-service to a Scheduled Task; it stays stopped until kcap local start."
            : "Moved the local server from kcap-service to a Scheduled Task, which runs it while you are signed in.";
    }

    static LocalInstallation? Installed(LocalRoot root) =>
        LocalStateFiles.ReadRegistry(root) is StateRead<InstallRegistry>.Present { Value.Active: { } active } ? active : null;
}

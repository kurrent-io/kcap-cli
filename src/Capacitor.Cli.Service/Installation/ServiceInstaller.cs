using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using static Capacitor.Cli.Service.Native.Win32;

namespace Capacitor.Cli.Service.Installation;

/// <summary>
/// The elevated half of the privileged helper: copies the running <c>kcap-service.exe</c> into the
/// administrator-only install directory and registers it. It takes no path argument — the source is
/// always the executable running this code, and the target is fixed
/// </summary>
[SupportedOSPlatform("windows")]
static unsafe class ServiceInstaller {
    public const int NotElevatedExit = 5;
    static readonly TimeSpan StateWait = TimeSpan.FromSeconds(30);

    /// <summary>Under Program Files as the machine records it. The elevated process's own environment is built
    /// from the user's, which a process without elevation can change, and would pick where SYSTEM's binary goes</summary>
    public static string InstallDirectory => ServiceIdentity.InstallDirectory(ProgramFiles());

    static string ProgramFiles() {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
        return key?.GetValue("ProgramFilesDir") is string path && Path.IsPathFullyQualified(path) ? path : @"C:\Program Files";
    }

    public static string InstalledBinary => Path.Combine(InstallDirectory, ServiceIdentity.ExecutableName);

    /// <summary><c>Program Files\kcap</c>, which holds nothing but the service's directory</summary>
    static string ProductDirectory => Path.GetDirectoryName(InstallDirectory)!;

    static bool IsElevated() {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static int Install(string sourceExe) {
        if (!IsElevated()) {
            Console.Error.WriteLine("kcap-service install needs an elevated (administrator) prompt");
            return NotElevatedExit;
        }

        // A removal since the last restart queued these files for deletion, and the plan below may find the
        // binary current and keep it: left queued, the restart would delete the service this install leaves.
        if (CancelPendingRemoval()) Console.WriteLine("install: cancelled the removal queued for the next restart");

        using var service = Existing();
        var current = File.Exists(InstalledBinary) && SameBytes(sourceExe, InstalledBinary);
        var running = service is not null && service.Status == ServiceControllerStatus.Running;
        var steps = InstallPlan.For(service is not null, current, running);

        try {
            RunSteps(steps, service, sourceExe);
        } catch {
            // The service was stopped for the upgrade; left that way, nothing supervises anyone's daemon
            if (running) StartAgain();
            throw;
        }

        Console.WriteLine(steps.Count == 0 ? "kcap-service is installed, current and running" : "kcap-service is installed and running");
        return 0;
    }

    static void RunSteps(IReadOnlyList<InstallStep> steps, ServiceController? service, string sourceExe) {
        foreach (var step in steps) {
            Console.WriteLine($"install: {step}");
            switch (step) {
                case InstallStep.StopService:
                    if (service!.Status != ServiceControllerStatus.Stopped) {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, StateWait);
                    }
                    break;
                case InstallStep.PrepareDirectory:
                    ProtectedDirectory.Ensure(InstallDirectory);
                    break;
                case InstallStep.CopyBinary:
                    CopyInto(sourceExe, InstalledBinary);
                    break;
                case InstallStep.CreateService:
                    Create();
                    break;
                case InstallStep.ConfigureService:
                    Configure();
                    break;
                case InstallStep.StartService:
                    using (var started = Existing()!) {
                        started.Start();
                        started.WaitForStatus(ServiceControllerStatus.Running, StateWait);
                    }
                    break;
            }
        }
    }

    static void StartAgain() {
        try {
            using var service = Existing();
            if (service is null || service.Status != ServiceControllerStatus.Stopped) return;
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, StateWait);
            Console.Error.WriteLine("install failed; kcap-service was started again as it was");
        } catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException) {
            Console.Error.WriteLine($"install failed, and starting kcap-service again also failed: {ex.Message}");
        }
    }

    public static int Uninstall() {
        if (!IsElevated()) {
            Console.Error.WriteLine("kcap-service uninstall needs an elevated (administrator) prompt");
            return NotElevatedExit;
        }

        using (var service = Existing()) {
            if (service is not null && service.Status != ServiceControllerStatus.Stopped) {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, StateWait);
            }
        }
        Delete();
        RemoveFiles(InstallDirectory);
        // The index stays: the users still in it keep their enrollments, and a reinstalled service that could
        // not see them would remove itself at the next unenroll
        Console.WriteLine("kcap-service is uninstalled; the daemons it started keep running as their users' own processes");
        return 0;
    }

    /// <summary>Run by the service itself once the last user has gone: the registration goes when the
    /// service stops, the files at the next restart</summary>
    public static void RemoveSelf() {
        Delete();
        QueueRemoval(InstallDirectory);
        Registry.LocalMachine.DeleteSubKeyTree(Path.GetDirectoryName(ServiceIdentity.IndexKey)!, throwOnMissingSubKey: false);
    }

    /// <summary>The queue runs in order, so the product directory, empty by then, goes after the service's</summary>
    static void QueueRemoval(string directory) {
        foreach (var file in Directory.EnumerateFiles(directory)) MoveFileExW(file, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        MoveFileExW(directory, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        MoveFileExW(ProductDirectory, null, MOVEFILE_DELAY_UNTIL_REBOOT);
    }

    static bool CancelPendingRemoval() {
        using var key = Registry.LocalMachine.OpenSubKey(PendingRemovals.KeyPath, writable: true);
        if (key?.GetValue(PendingRemovals.ValueName) is not string[] operations) return false;
        var kept = PendingRemovals.Without(operations, ProductDirectory);
        if (kept.Length == operations.Length) return false;
        if (kept.Length == 0) key.DeleteValue(PendingRemovals.ValueName, throwOnMissingValue: false);
        else key.SetValue(PendingRemovals.ValueName, kept, RegistryValueKind.MultiString);
        return true;
    }

    static ServiceController? Existing() {
        var service = new ServiceController(ServiceIdentity.ServiceName);
        try {
            _ = service.Status;
            return service;
        } catch (InvalidOperationException) {
            service.Dispose();
            return null;
        }
    }

    static bool SameBytes(string a, string b) {
        using var first = File.OpenRead(a);
        using var second = File.OpenRead(b);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    /// <summary>A stopped service's image can stay mapped for a moment after it reports Stopped</summary>
    static void CopyInto(string source, string target) {
        var staged = target + ".new";
        File.Copy(source, staged, overwrite: true);
        for (var attempt = 1; ; attempt++) {
            try {
                File.Move(staged, target, overwrite: true);
                return;
            } catch (IOException) when (attempt < 20) {
                Thread.Sleep(250);
            } catch (UnauthorizedAccessException) when (attempt < 20) {
                Thread.Sleep(250);
            }
        }
    }

    static string BinaryPathArgument => $"\"{InstalledBinary}\"";

    static void Create() {
        var manager = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (manager == IntPtr.Zero) throw Failure("OpenSCManager");
        try {
            var service = CreateServiceW(manager, ServiceIdentity.ServiceName, ServiceIdentity.DisplayName, SERVICE_ALL_ACCESS,
                SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL, BinaryPathArgument, null, IntPtr.Zero, null, null, null);
            if (service == IntPtr.Zero) throw Failure("CreateService");
            try {
                ApplySettings(service);
            } finally {
                CloseServiceHandle(service);
            }
        } finally {
            CloseServiceHandle(manager);
        }
    }

    /// <summary>Puts an existing registration back to what this release registers, whatever changed it</summary>
    static void Configure() => WithService(SERVICE_ALL_ACCESS, service => {
        if (!ChangeServiceConfigW(service, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
                BinaryPathArgument, null, IntPtr.Zero, null, "LocalSystem", null, ServiceIdentity.DisplayName))
            throw Failure("ChangeServiceConfig");
        ApplySettings(service);
    });

    static void ApplySettings(IntPtr service) {
        var description = new SERVICE_DESCRIPTION { lpDescription = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(ServiceIdentity.Description) };
        try {
            if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, &description)) throw Failure("ChangeServiceConfig2(description)");
        } finally {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(description.lpDescription);
        }

        var delayed = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = 1 };
        if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, &delayed)) throw Failure("ChangeServiceConfig2(delayed start)");

        var actions = stackalloc SC_ACTION[3];
        actions[0] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 5_000 };
        actions[1] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 5_000 };
        actions[2] = new SC_ACTION { Type = SC_ACTION_RESTART, Delay = 30_000 };
        var failure = new SERVICE_FAILURE_ACTIONS { dwResetPeriod = 86_400, cActions = 3, lpsaActions = actions };
        if (!ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, &failure)) throw Failure("ChangeServiceConfig2(failure actions)");
    }

    static void Delete() => WithService(DELETE, service => {
        if (!DeleteService(service) && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != ERROR_SERVICE_MARKED_FOR_DELETE)
            throw Failure("DeleteService");
    });

    static void WithService(uint access, Action<IntPtr> action) {
        var manager = OpenSCManagerW(null, null, SC_MANAGER_ALL_ACCESS);
        if (manager == IntPtr.Zero) throw Failure("OpenSCManager");
        try {
            var service = OpenServiceW(manager, ServiceIdentity.ServiceName, access);
            if (service == IntPtr.Zero) {
                if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == ERROR_SERVICE_DOES_NOT_EXIST) return;
                throw Failure("OpenService");
            }
            try {
                action(service);
            } finally {
                CloseServiceHandle(service);
            }
        } finally {
            CloseServiceHandle(manager);
        }
    }

    static void RemoveFiles(string directory) {
        if (!Directory.Exists(directory)) return;
        try {
            Directory.Delete(directory, recursive: true);
            if (Directory.Exists(ProductDirectory) && !Directory.EnumerateFileSystemEntries(ProductDirectory).Any())
                Directory.Delete(ProductDirectory);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            QueueRemoval(directory);
            Console.WriteLine($"{directory} is removed at the next restart");
        }
    }
}

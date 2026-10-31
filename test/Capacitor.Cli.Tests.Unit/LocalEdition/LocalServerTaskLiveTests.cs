using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>
/// The local server's Scheduled Task running the real server: it installs, reports ready, comes back after the server
/// is killed, stops through Ctrl+Break with the stack verified down, and uninstalls. Opt-in with
/// <c>KCAP_LOCAL_SERVICE_CHECK=1</c> and <c>KCAP_LOCAL_SERVICE_BUNDLE</c> naming an extracted win-x64 bundle, because it
/// registers a logon task for the user running it, and a Windows server cannot be stood in for by a script. The task
/// has a name of its own, so an installation on that machine is untouched. It lives here because the integration
/// assembly does not run on Windows.
/// </summary>
[RunOn(OS.Windows)]
[NotInParallel(nameof(LocalServerTaskLiveTests))]
public class LocalServerTaskLiveTests {
    const string CheckEnvVar  = "KCAP_LOCAL_SERVICE_CHECK";
    const string BundleEnvVar = "KCAP_LOCAL_SERVICE_BUNDLE";

    /// <summary>A first start creates the database cluster.</summary>
    static readonly TimeSpan ReadyBudget = TimeSpan.FromMinutes(3);

    [TempDir("lst")] public required TempDir Tmp { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task The_task_starts_restarts_after_a_crash_and_stops_through_its_console() {
        Skip.When(Environment.GetEnvironmentVariable(CheckEnvVar) != "1",
            $"Set {CheckEnvVar}=1 to register a test task with this machine's Task Scheduler.");
        var bundle = Environment.GetEnvironmentVariable(BundleEnvVar);
        Skip.When(string.IsNullOrEmpty(bundle), $"Set {BundleEnvVar} to an extracted win-x64 local bundle.");

        var root    = new LocalRoot(Tmp.PathTo("local"));
        var current = Path.Combine(Tmp.CreateDir("local/bin"), "current");
        Junction(current, bundle!);

        var unit    = LocalServerUnit.ForInstallation(root, FreePort()) with { TaskName = $"kcap-local-test-{Guid.NewGuid().ToString("N")[..8]}" };
        var service = new LocalServerTaskService(new UserHome(Tmp.CreateDir("home")), TimeProvider.System, breakConsole: Break);
        using var http = new HttpClient();

        try {
            service.Install(unit);
            await LocalServerReadiness.WaitAsync(http, unit, ReadyBudget, TimeProvider.System, CancellationToken.None);
            var first = RunningPid(service, unit);

            using (var crashed = Process.GetProcessById(first)) crashed.Kill();
            var restarted = await WaitForNewPidAsync(service, unit, first);
            var identity  = PidIdentity.Capture(restarted);
            await LocalServerReadiness.WaitAsync(http, unit, ReadyBudget, TimeProvider.System, CancellationToken.None);

            await service.StopAsync(unit, CancellationToken.None);
            await Assert.That(LocalStackProbe.FindLive(root)).IsNull();
            await Assert.That(PidIdentity.IsGone(restarted, identity)).IsTrue();
            await Assert.That(service.Activity(unit)).IsEqualTo(UnitActivity.Stopped.Instance);

            service.Start(unit);
            await LocalServerReadiness.WaitAsync(http, unit, ReadyBudget, TimeProvider.System, CancellationToken.None);

            await service.UninstallAsync(unit, CancellationToken.None);
            await Assert.That(service.IsInstalled(unit)).IsFalse();
            await Assert.That(LocalStackProbe.FindLive(root)).IsNull();
        } finally {
            try { await service.UninstallAsync(unit, CancellationToken.None); } catch (LocalLifecycleException) { /* reported above */ }

            // The link alone, so the temp directory's cleanup never walks into the bundle.
            Directory.Delete(current);
        }
    }

    /// <summary>The sender leaves its console, so it is the real kcap, never the test host.</summary>
    string? Break(int pid) {
        using var sender = Process.Start(KcapProcess.StartInfo(Daemons.Store, Config.Root,
            "local", "console-break", pid.ToString(CultureInfo.InvariantCulture)))!;
        sender.StandardInput.Close();
        var error = sender.StandardError.ReadToEnd();
        sender.StandardOutput.ReadToEnd();
        sender.WaitForExit();

        return sender.ExitCode == 0 ? null : $"exit {sender.ExitCode}: {error.Trim()}";
    }

    static void Junction(string link, string target) {
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(a);

        using var mklink = Process.Start(info)!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        if (mklink.ExitCode != 0) throw new IOException($"mklink /J {link} exited {mklink.ExitCode}");
    }

    static int FreePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static int RunningPid(LocalServerTaskService service, LocalServerUnit unit) =>
        service.Activity(unit) is UnitActivity.Running running
            ? running.Pid
            : throw new InvalidOperationException($"The task's server is not running: {service.Activity(unit)}");

    /// <summary>The wrapper pauses before it relaunches, and the new server first stops the Postgres the killed one left.</summary>
    static async Task<int> WaitForNewPidAsync(LocalServerTaskService service, LocalServerUnit unit, int previous) {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromMinutes(2);

        while (TimeProvider.System.GetUtcNow() < deadline) {
            if (service.Activity(unit) is UnitActivity.Running { Pid: var pid } && pid != previous) return pid;

            await Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, CancellationToken.None);
        }

        throw new TimeoutException("The task's wrapper did not relaunch the killed server.");
    }
}

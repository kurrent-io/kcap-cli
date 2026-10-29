using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Integration.LocalEdition;

/// <summary>
/// The local server's unit under the real launchd or systemd: it installs, reports ready, comes back after
/// the server is killed, and stops with the stack verified down. Opt-in with
/// <c>KCAP_LOCAL_SERVICE_CHECK=1</c>, because it loads a user service on the machine running it. The unit has
/// a name of its own, so an installation on that machine is untouched, and the server is a stub that holds
/// the instance lock and answers the readiness probe. On Linux the unit is written under the real home,
/// since systemd reads user units only from there.
/// </summary>
[NotInParallel(nameof(LocalServerServiceLiveTests))]
public class LocalServerServiceLiveTests {
    const string CheckEnvVar = "KCAP_LOCAL_SERVICE_CHECK";

    static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(60);

    [TempDir("lss")] public required TempDir Tmp { get; init; }

    const string Stub = """
        #!/usr/bin/env python3
        import fcntl, http.server, os, signal, threading
        root = os.environ["KCAP_LOCAL_ROOT"]
        run = os.path.join(root, "run")
        os.makedirs(run, exist_ok=True)
        lock = open(os.path.join(run, "server.lock"), "a+")
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        with open(os.path.join(run, "stub.pid"), "w") as f:
            f.write(str(os.getpid()))
        class Ready(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200 if self.path == "/healthz/ready" else 404)
                self.end_headers()
            def log_message(self, *args):
                pass
        server = http.server.HTTPServer(("127.0.0.1", int(os.environ["Local__Port"])), Ready)
        signal.signal(signal.SIGTERM, lambda *_: threading.Thread(target=server.shutdown).start())
        server.serve_forever()
        """;

    [Test]
    public async Task The_unit_starts_restarts_after_a_crash_and_stops_verified() {
        Skip.When(Environment.GetEnvironmentVariable(CheckEnvVar) != "1",
            $"Set {CheckEnvVar}=1 to load a test unit into this machine's launchd or systemd.");
        Skip.When(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux(), "The local edition runs on macOS and Linux.");

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var root   = new LocalRoot(Tmp.PathTo("local"));
        var unit   = LocalServerUnit.ForInstallation(root, FreePort()) with {
            Label       = $"io.kurrent.kcap.local-test-{suffix}",
            SystemdName = $"kcap-local-test-{suffix}.service",
        };
        Tmp.CreateExecutable("local/bin/current/kcap-server", Stub);

        var home    = OperatingSystem.IsMacOS() ? new UserHome(Tmp.CreateDir("home")) : UserHome.FromEnvironment();
        var service = LocalServerServices.ForCurrentOs(home, TimeProvider.System);
        using var http = new HttpClient();

        try {
            service.Install(unit);
            await LocalServerReadiness.WaitAsync(http, unit, ReadyBudget, TimeProvider.System, CancellationToken.None);
            var first = StubPid(root);

            using (var crashed = Process.GetProcessById(first)) crashed.Kill();
            var restarted = await WaitForNewPidAsync(root, first);
            var identity  = PidIdentity.Capture(restarted);
            await LocalServerReadiness.WaitAsync(http, unit, ReadyBudget, TimeProvider.System, CancellationToken.None);

            await service.StopAsync(unit, CancellationToken.None);
            await Assert.That(LocalStackProbe.FindLive(root)).IsNull();
            await Assert.That(PidIdentity.IsGone(restarted, identity)).IsTrue();

            service.Start(unit);
            await LocalServerReadiness.WaitAsync(http, unit, ReadyBudget, TimeProvider.System, CancellationToken.None);

            await service.UninstallAsync(unit, CancellationToken.None);
            await Assert.That(service.IsInstalled(unit)).IsFalse();
            await Assert.That(LocalStackProbe.FindLive(root)).IsNull();
        } finally {
            try { await service.UninstallAsync(unit, CancellationToken.None); } catch (LocalLifecycleException) { /* reported above */ }
        }
    }

    static int FreePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    static int StubPid(LocalRoot root) =>
        int.Parse(File.ReadAllText(Path.Combine(root.Directory, "run", "stub.pid")), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>launchd waits out its throttle interval before relaunching a job that died.</summary>
    static async Task<int> WaitForNewPidAsync(LocalRoot root, int previous) {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(60);

        while (TimeProvider.System.GetUtcNow() < deadline) {
            try {
                if (StubPid(root) is var pid && pid != previous) return pid;
            } catch (Exception ex) when (ex is IOException or FormatException) {
                // Mid-write by the relaunched stub.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TimeProvider.System, CancellationToken.None);
        }

        throw new TimeoutException("The service manager did not relaunch the killed server.");
    }
}

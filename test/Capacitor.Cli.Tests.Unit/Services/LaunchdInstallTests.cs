using System.Runtime.InteropServices;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A plain install boots the job out and bootstraps it again. <c>bootout</c> returns while a running job
/// is still exiting, and a bootstrap before launchd lets go of the label fails with EIO, so the install
/// waits for the label to unload and touches the plist only once it has.
/// </summary>
public partial class LaunchdInstallTests {
    [TempHome] public required TempHome Home { get; init; }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    static readonly (int, string, string) Loaded = (0, "state = running\npid = 924\n", "");
    static readonly (int, string, string) Absent = (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501");

    static ServiceSpec Spec() => new("test", "/x/kcap-daemon", "/x/log", new Dictionary<string, string>(), []);

    [Test]
    public async Task Install_bootstraps_only_once_the_booted_out_job_has_unloaded() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var prints = 0;
        List<string> verbs = [];
        var mgr = new LaunchdServiceManager(Home, TimeProvider.System, runProcess: (_, args) => {
            verbs.Add(args[0]);
            return args[0] == "print" ? ++prints < 3 ? Loaded : Absent : (0, "", "");
        });

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(ok).IsTrue();
        await Assert.That(verbs).IsEquivalentTo(["bootout", "print", "print", "print", "bootstrap"]);
        await Assert.That(File.Exists(LaunchdUnit.PlistPath(Home, "test"))).IsTrue();
    }

    [Test]
    public async Task Install_leaves_the_plist_alone_when_the_job_stays_loaded() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var time = new FakeTimeProvider();
        List<string> verbs = [];
        var mgr = new LaunchdServiceManager(Home, time, runProcess: (_, args) => {
            verbs.Add(args[0]);
            if (args[0] != "print") return (0, "", "");
            time.Advance(LaunchdServiceManager.UnloadWait / 2);
            return Loaded;
        });

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("still had the job loaded");
        await Assert.That(verbs).DoesNotContain("bootstrap");
        await Assert.That(File.Exists(LaunchdUnit.PlistPath(Home, "test"))).IsFalse();
    }

    /// <summary>The job is a per-user agent: launchctl's "re-run as root" hint would send the user to
    /// root's own domain, so it is dropped from the reported error.</summary>
    [Test]
    public async Task A_failed_bootstrap_is_reported_without_the_root_hint() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var mgr = new LaunchdServiceManager(Home, TimeProvider.System, runProcess: (_, args) => args[0] switch {
            "print"     => Absent,
            "bootstrap" => (5, "", "Bootstrap failed: 5: Input/output error\nTry re-running the command as root for richer errors.\n"),
            _           => (0, "", ""),
        });

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsEqualTo("launchctl bootstrap failed (exit 5): Bootstrap failed: 5: Input/output error");
    }
}

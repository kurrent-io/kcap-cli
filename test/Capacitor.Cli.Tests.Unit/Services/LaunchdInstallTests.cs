using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A plain install boots the job out and bootstraps it again. <c>bootout</c> returns while a running job
/// is still exiting, and a bootstrap before launchd lets go of the label fails with EIO, so the install
/// waits for the label to unload, inside one deadline, and touches the plist only once it has.
/// </summary>
public class LaunchdInstallTests {
    [TempHome] public required TempHome Home { get; init; }

    static readonly (int, string, string, bool) Loaded = (0, "state = running\npid = 924\n", "", false);
    static readonly (int, string, string, bool) Absent = (113, "", "Could not find service \"io.kurrent.kcap.daemon.test\" in domain for user gui: 501", false);

    static ServiceSpec Spec() => new("test", "/x/kcap-daemon", "/x/log", new Dictionary<string, string>(), []);

    /// <summary>Every launchctl call, bounded or not, goes through <paramref name="answer"/>; the verbs
    /// are recorded in order.</summary>
    LaunchdServiceManager Manager(TimeProvider time, List<string> verbs, Func<string, TimeSpan?, (int, string, string, bool)> answer) =>
        new(Home, time,
            runProcess: (_, args) => {
                verbs.Add(args[0]);
                var (code, stdout, stderr, _) = answer(args[0], null);
                return (code, stdout, stderr);
            },
            runBounded: (_, args, timeout) => {
                verbs.Add(args[0]);
                return answer(args[0], timeout);
            });

    [Test]
    public async Task Install_bootstraps_only_once_the_booted_out_job_has_unloaded() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var prints = 0;
        List<string> verbs = [];
        var mgr = Manager(TimeProvider.System, verbs, (verb, _) => verb == "print"
            ? ++prints < 3 ? Loaded : Absent
            : (0, "", "", false));

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(ok).IsTrue();
        await Assert.That(verbs).IsEquivalentTo(["bootout", "print", "print", "print", "bootstrap"], CollectionOrdering.Matching);
        await Assert.That(File.Exists(LaunchdUnit.PlistPath(Home, "test"))).IsTrue();
    }

    [Test]
    public async Task Install_leaves_the_plist_alone_when_the_job_stays_loaded() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var time = new FakeTimeProvider();
        List<string> verbs = [];
        var mgr = Manager(time, verbs, (verb, _) => {
            if (verb != "print") return (0, "", "", false);
            time.Advance(LaunchdServiceManager.UnloadWait / 2);
            return Loaded;
        });

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("still had the job loaded");
        await Assert.That(verbs).DoesNotContain("bootstrap");
        await Assert.That(File.Exists(LaunchdUnit.PlistPath(Home, "test"))).IsFalse();
    }

    /// <summary>A hung launchctl cannot carry the install past its deadline: bootout and each probe get
    /// only what is left of it, and a probe killed on timeout is an unknown answer, not an unload.</summary>
    [Test]
    public async Task A_hung_probe_runs_out_the_deadline_and_leaves_the_plist_alone() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var time = new FakeTimeProvider();
        List<string> verbs = [];
        List<TimeSpan?> timeouts = [];
        var mgr = Manager(time, verbs, (verb, timeout) => {
            timeouts.Add(timeout);
            if (verb != "print") return (0, "", "", false);
            time.Advance(timeout!.Value);
            return (137, "", "", true);
        });

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("could not say whether the job unloaded");
        await Assert.That(verbs).IsEquivalentTo(["bootout", "print"], CollectionOrdering.Matching);
        TimeSpan?[] budgets = [LaunchdServiceManager.UnloadWait, LaunchdServiceManager.UnloadWait];
        await Assert.That(timeouts).IsEquivalentTo(budgets, CollectionOrdering.Matching);
        await Assert.That(File.Exists(LaunchdUnit.PlistPath(Home, "test"))).IsFalse();
    }

    /// <summary>The job is a per-user agent: launchctl's "re-run as root" hint would send the user to
    /// root's own domain, so it is dropped from the reported error.</summary>
    [Test]
    public async Task A_failed_bootstrap_is_reported_without_the_root_hint() {
        Skip.When(OperatingSystem.IsWindows(), "Uid() P/Invokes libc's getuid, POSIX-only");

        var mgr = Manager(TimeProvider.System, [], (verb, _) => verb switch {
            "print"     => Absent,
            "bootstrap" => (5, "", "Bootstrap failed: 5: Input/output error\nTry re-running the command as root for richer errors.\n", false),
            _           => (0, "", "", false),
        });

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).IsEqualTo("launchctl bootstrap failed (exit 5): Bootstrap failed: 5: Input/output error");
    }
}

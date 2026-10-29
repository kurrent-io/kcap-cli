using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>The query reports the loaded job's spawn type word, and nothing when the label is not loaded
/// or the print has no such line.</summary>
public class LaunchdQueryTests {
    [TempHome] public required TempHome Home { get; init; }

    LaunchdServiceManager Manager(int exit, string stdout, string stderr = "") =>
        new(Home, TimeProvider.System, runProcess: (_, _) => (exit, stdout, stderr));

    [Test]
    public async Task Loaded_job_reports_its_spawn_type() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var q = Manager(0, "gui/501/io.kurrent.kcap.daemon.test = {\n\tstate = running\n\tpid = 7\n\tspawn type = adaptive (6)\n}\n").Query("test");
        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Loaded);
        await Assert.That(q.LoadedSpawnType).IsEqualTo("adaptive");
    }

    [Test]
    public async Task Loaded_job_without_the_line_reports_null() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var q = Manager(0, "gui/501/io.kurrent.kcap.daemon.test = {\n\tstate = running\n}\n").Query("test");
        await Assert.That(q.LoadedSpawnType).IsNull();
    }

    [Test]
    public async Task Absent_label_reports_null() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var q = Manager(113, "", "Could not find service").Query("test");
        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Absent);
        await Assert.That(q.LoadedSpawnType).IsNull();
    }
}

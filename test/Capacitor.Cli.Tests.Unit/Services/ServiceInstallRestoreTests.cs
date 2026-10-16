using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>A registration the OS tool refuses puts the previous unit file back on systemd and Windows,
/// so a failed install does not leave an existing unit repointed at the next start.</summary>
public class ServiceInstallRestoreTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ServiceSpec Spec() => new("test", "/new/kcap-daemon", "/x/log", new Dictionary<string, string>(), []);

    [Test]
    public async Task Systemd_restores_the_previous_unit_when_enable_is_refused() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "previous unit");

        var mgr = new SystemdServiceManager(Home, runProcess: (_, args) =>
            args.Contains("enable") ? (1, "", "Failed to enable unit") : (0, "", ""));

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("Failed to enable unit");
        await Assert.That(File.ReadAllText(path)).IsEqualTo("previous unit");
    }

    /// <summary>When the reload that would load the restored file is refused too, the error says systemd
    /// may still hold the new definition, since the file alone no longer describes what it runs.</summary>
    [Test]
    public async Task Systemd_reports_a_refused_rollback_reload() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "previous unit");

        var reloads = 0;
        var mgr = new SystemdServiceManager(Home, runProcess: (_, args) =>
            args.Contains("enable") ? (1, "", "Failed to enable unit")
            : args.Contains("daemon-reload") && ++reloads == 2 ? (1, "", "Access denied")
            : (0, "", ""));

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("the previous unit file was put back, but systemctl --user daemon-reload failed");
        await Assert.That(File.ReadAllText(path)).IsEqualTo("previous unit");
    }

    [Test]
    public async Task Windows_restores_the_previous_wrapper_when_create_is_refused() {
        var wrapper = WindowsTaskUnit.WrapperPath(Config.Root, "test");
        File.WriteAllText(wrapper, "previous wrapper");

        var mgr = new WindowsScheduledTaskServiceManager(Config.Root, schtasks: args =>
            args[0] == "/Create" ? (1, "", "ERROR: Access is denied.") : (0, "", ""));

        var ok = mgr.Install(Spec(), startNow: true, out var error);

        await Assert.That(ok).IsFalse();
        await Assert.That(error).Contains("Access is denied");
        await Assert.That(File.ReadAllText(wrapper)).IsEqualTo("previous wrapper");
    }

    [Test]
    public async Task Windows_removes_a_wrapper_it_wrote_for_a_refused_new_task() {
        var wrapper = WindowsTaskUnit.WrapperPath(Config.Root, "test");

        var mgr = new WindowsScheduledTaskServiceManager(Config.Root, schtasks: args =>
            args[0] == "/Create" ? (1, "", "ERROR: Access is denied.") : (0, "", ""));

        await Assert.That(mgr.Install(Spec(), startNow: true, out _)).IsFalse();
        await Assert.That(File.Exists(wrapper)).IsFalse();
    }
}

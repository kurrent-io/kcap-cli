using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class SystemdUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        id, "/opt/kcap/kcap-daemon", "/home/u/.config/kcap/daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["KCAP_PROFILE"] = "work" },
        ["--max-agents", "8"]);

    [Test]
    public async Task UnitName_is_per_instance() {
        await Assert.That(SystemdUnit.UnitName("laptop")).IsEqualTo("kcap-daemon-laptop.service");
    }

    [Test]
    public async Task Unit_has_execstart_restart_and_env() {
        var unit = SystemdUnit.Unit(Spec());
        await Assert.That(unit).Contains("ExecStart=/opt/kcap/kcap-daemon --name laptop --log-file /home/u/.config/kcap/daemon-laptop.log --max-agents 8");
        await Assert.That(unit).Contains("Restart=on-failure");
        await Assert.That(unit).Contains("Environment=PATH=/usr/bin");
        await Assert.That(unit).Contains("Environment=KCAP_PROFILE=work");
        await Assert.That(unit).Contains("WantedBy=default.target");
    }

    [Test]
    public async Task IdFromUnitFileName_extracts_id() {
        await Assert.That(SystemdUnit.IdFromUnitFileName("kcap-daemon-laptop.service")).IsEqualTo("laptop");
        await Assert.That(SystemdUnit.IdFromUnitFileName("other.service")).IsNull();
    }

    [Test]
    public async Task StatusFrom_maps_active_strings() {
        await Assert.That(SystemdUnit.StatusFrom(activeOut: "active", enabledExit: 0)).IsEqualTo(ServiceState.Running);
        await Assert.That(SystemdUnit.StatusFrom("inactive", 0)).IsEqualTo(ServiceState.Installed);
        await Assert.That(SystemdUnit.StatusFrom("inactive", 1)).IsEqualTo(ServiceState.NotInstalled);
    }
}

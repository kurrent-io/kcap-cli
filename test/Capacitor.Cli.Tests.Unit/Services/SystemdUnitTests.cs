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
    public async Task Unit_quotes_values_and_args_with_spaces() {
        var spec = Spec() with {
            DaemonBinaryPath = "/opt/k cap/kcap-daemon",
            LogPath          = "/home/u/my logs/daemon-laptop.log",
            Environment      = new Dictionary<string, string> { ["PATH"] = "/a b:/c", ["KCAP_PROFILE"] = "work" },
        };
        var unit = SystemdUnit.Unit(spec);
        await Assert.That(unit).Contains("Environment=\"PATH=/a b:/c\"");
        await Assert.That(unit).Contains("Environment=KCAP_PROFILE=work"); // no space → unquoted
        await Assert.That(unit).Contains("ExecStart=\"/opt/k cap/kcap-daemon\" --name laptop --log-file \"/home/u/my logs/daemon-laptop.log\" --max-agents 8");
    }

    [Test]
    public async Task IdFromUnitFileName_extracts_id() {
        await Assert.That(SystemdUnit.IdFromUnitFileName("kcap-daemon-laptop.service")).IsEqualTo("laptop");
        await Assert.That(SystemdUnit.IdFromUnitFileName("other.service")).IsNull();
    }

    [Test]
    public async Task BinaryFromUnit_parses_unquoted_and_quoted_execstart() {
        await Assert.That(SystemdUnit.BinaryFromUnit(SystemdUnit.Unit(Spec()))).IsEqualTo("/opt/kcap/kcap-daemon");

        var spaced = Spec() with { DaemonBinaryPath = "/opt/k cap/kcap-daemon" };
        await Assert.That(SystemdUnit.BinaryFromUnit(SystemdUnit.Unit(spaced))).IsEqualTo("/opt/k cap/kcap-daemon");
    }

    [Test]
    public async Task StatusFrom_maps_active_strings() {
        await Assert.That(SystemdUnit.StatusFrom(activeOut: "active", enabledExit: 0)).IsEqualTo(ServiceState.Running);
        await Assert.That(SystemdUnit.StatusFrom("inactive", 0)).IsEqualTo(ServiceState.Installed);
        await Assert.That(SystemdUnit.StatusFrom("inactive", 1)).IsEqualTo(ServiceState.NotInstalled);
    }

    [Test]
    public async Task QueryFromShow_reports_the_running_daemons_pid() {
        var q = SystemdUnit.QueryFromShow(0, "ActiveState=active\nUnitFileState=enabled\nMainPID=4242\n", unitPresent: true, "/opt/kcap/kcap-daemon");

        await Assert.That(q).IsEqualTo(new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/opt/kcap/kcap-daemon", 4242));
    }

    [Test]
    public async Task QueryFromShow_stopped_unit_is_loaded_without_a_pid() {
        var q = SystemdUnit.QueryFromShow(0, "ActiveState=inactive\nUnitFileState=enabled\nMainPID=0\n", unitPresent: true, null);

        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Loaded);
        await Assert.That(q.State).IsEqualTo(ServiceState.Installed);
        await Assert.That(q.JobPid).IsNull();
    }

    [Test]
    public async Task QueryFromShow_unknown_unit_is_absent() {
        var q = SystemdUnit.QueryFromShow(0, "ActiveState=inactive\nUnitFileState=\nMainPID=0\n", unitPresent: false, null);

        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Absent);
        await Assert.That(q.State).IsEqualTo(ServiceState.NotInstalled);
    }

    /// <summary>Without a user bus nothing is known about the unit, so it must not read as free to install over.</summary>
    [Test]
    public async Task QueryFromShow_failed_systemctl_is_unknown() {
        var q = SystemdUnit.QueryFromShow(1, "", unitPresent: true, "/opt/kcap/kcap-daemon");

        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Unknown);
        await Assert.That(q.UnitPresent).IsTrue();
        await Assert.That(q.JobPid).IsNull();
    }

    [Test]
    public async Task EnvFromUnit_reads_back_what_Unit_wrote() {
        var env = new Dictionary<string, string> {
            ["PATH"]         = "/a b:/c",
            ["KCAP_PROFILE"] = "work",
            ["QUOTED"]       = "say \"hi\" O'Reilly",
            ["SLASHES"]      = @"C:\x\y",
            ["PERCENT"]      = "50%off $HOME",
            ["EQUALS"]       = "a=b",
        };

        var parsed = SystemdUnit.EnvFromUnit(SystemdUnit.Unit(Spec() with { Environment = env }));

        await Assert.That(parsed).IsEquivalentTo(env);
    }

    /// <summary>A daemon in its restart delay or still being stopped may be alive, so it never reads as stopped.</summary>
    [Test]
    [Arguments("activating")]
    [Arguments("deactivating")]
    [Arguments("maintenance")]
    [Arguments("")]
    public async Task QueryFromShow_transitional_state_is_unknown_and_running(string activeState) {
        var q = SystemdUnit.QueryFromShow(0, $"ActiveState={activeState}\nUnitFileState=enabled\nMainPID=4242\n", unitPresent: true, null);

        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Unknown);
        await Assert.That(q.State).IsEqualTo(ServiceState.Running);
        await Assert.That(q.JobPid).IsNull();
    }

    [Test]
    public async Task QueryFromShow_reloading_unit_is_running_with_its_pid() {
        var q = SystemdUnit.QueryFromShow(0, "ActiveState=reloading\nUnitFileState=enabled\nMainPID=4242\n", unitPresent: true, null);

        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Loaded);
        await Assert.That(q.JobPid).IsEqualTo(4242);
    }

    [Test]
    public async Task QueryFromShow_failed_unit_is_stopped() {
        var q = SystemdUnit.QueryFromShow(0, "ActiveState=failed\nUnitFileState=enabled\nMainPID=0\n", unitPresent: true, null);

        await Assert.That(q.Probe).IsEqualTo(LabelProbe.Loaded);
        await Assert.That(q.State).IsEqualTo(ServiceState.Installed);
    }
}

using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class SystemdUnitTests {
    static ServiceSpec Spec(string id = "laptop") => new(
        id, "/opt/kcap/kcap-daemon", "/home/u/.config/kcap/daemon-laptop.log",
        new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["KCAP_PROFILE"] = "work" },
        ["--max-agents", "8"]);

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
    public async Task MainPid_treats_zero_as_none() {
        await Assert.That(SystemdUnit.MainPid("4242\n")).IsEqualTo(4242);
        await Assert.That(SystemdUnit.MainPid("0\n")).IsNull();
        await Assert.That(SystemdUnit.MainPid("")).IsNull();
    }

    /// <summary>Quoting, escapes and doubled percent signs all round-trip, so a retire reads the same
    /// profile the install baked.</summary>
    [Test]
    public async Task EnvFromUnit_reverses_the_rendered_assignments() {
        var env = new Dictionary<string, string> {
            ["PATH"]         = "/a b:/c",
            ["KCAP_PROFILE"] = "work",
            ["QUOTED"]       = "say \"hi\" \\ 50%",
        };

        var read = SystemdUnit.EnvFromUnit(SystemdUnit.Unit(Spec() with { Environment = env }));

        await Assert.That(read).IsEquivalentTo(env);
    }

    [Test]
    public async Task IdFromUnitFileName_reverses_UnitName_and_ignores_other_units() {
        await Assert.That(SystemdUnit.IdFromUnitFileName(SystemdUnit.UnitName("laptop"))).IsEqualTo("laptop");
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
    public async Task WithEnvironmentValue_replaces_the_assignment_in_place() {
        var rewritten = SystemdUnit.WithEnvironmentValue(SystemdUnit.Unit(Spec()), "PATH", "/usr/bin:/a b");

        await Assert.That(rewritten).IsNotNull();
        await Assert.That(SystemdUnit.EnvFromUnit(rewritten!)["PATH"]).IsEqualTo("/usr/bin:/a b");
        await Assert.That(SystemdUnit.EnvFromUnit(rewritten!)["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(SystemdUnit.BinaryFromUnit(rewritten!)).IsEqualTo("/opt/kcap/kcap-daemon");
    }

    [Test]
    public async Task WithEnvironmentValue_adds_a_missing_assignment_ahead_of_ExecStart() {
        var unit = SystemdUnit.Unit(Spec() with { Environment = new Dictionary<string, string> { ["KCAP_PROFILE"] = "work" } });

        var rewritten = SystemdUnit.WithEnvironmentValue(unit, "PATH", "/usr/bin");
        var lines     = rewritten!.Split('\n').ToList();

        await Assert.That(SystemdUnit.EnvFromUnit(rewritten)["PATH"]).IsEqualTo("/usr/bin");
        await Assert.That(lines.IndexOf("Environment=PATH=/usr/bin"))
            .IsLessThan(lines.FindIndex(l => l.StartsWith("ExecStart=", StringComparison.Ordinal)));
    }

    [Test]
    public async Task WithEnvironmentValue_is_null_when_the_value_is_already_set() {
        await Assert.That(SystemdUnit.WithEnvironmentValue(SystemdUnit.Unit(Spec()), "PATH", "/usr/bin")).IsNull();
    }

    [Test]
    public async Task WithEnvironmentValue_refuses_a_line_carrying_other_assignments() {
        const string unit = "[Service]\nEnvironment=PATH=/usr/bin KCAP_PROFILE=work\nExecStart=/opt/kcap/kcap-daemon --name laptop\n";

        await Assert.That(SystemdUnit.WithEnvironmentValue(unit, "PATH", "/usr/bin:/opt/agents")).IsNull();
    }

    /// <summary>A unit someone ran `systemctl --user disable` on is still ours to start; one systemd finds
    /// without our file is not.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task StatusFrom_a_disabled_unit_is_installed_only_with_our_file(bool unitPresent) {
        var state = SystemdUnit.StatusFrom("inactive\n", enabledExit: 1, "disabled\n", unitPresent);

        await Assert.That(state).IsEqualTo(unitPresent ? ServiceState.Installed : ServiceState.NotInstalled);
    }
}

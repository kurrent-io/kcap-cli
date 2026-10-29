using Capacitor.Cli.Service.Sessions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Service.Tests.Unit.Sessions;

/// Windows path semantics: the service runs nowhere else
[RunOn(OS.Windows)]
public class EnrollmentTests {
    const string Daemon = @"C:\Users\alice\AppData\Local\kcap\current\kcap-daemon.exe";

    [Test]
    public async Task A_plain_name_and_a_local_daemon_path_parse() {
        var enrollment = Enrollment.Parse("laptop", Daemon, "work", 3, out var refusal);

        await Assert.That(refusal).IsNull();
        await Assert.That(enrollment).IsEqualTo(new Enrollment("laptop", Daemon, "work", 3));
        await Assert.That(enrollment!.CliPath).IsEqualTo(@"C:\Users\alice\AppData\Local\kcap\current\kcap.exe");
    }

    [Test]
    [Arguments(@"kcap\current\kcap-daemon.exe")]
    [Arguments(@"\\server\share\kcap-daemon.exe")]
    [Arguments(@"C:\Users\alice\AppData\Local\kcap\current\kcap.exe")]
    [Arguments("C:\\Users\\a\"b\\kcap-daemon.exe")]
    [Arguments("")]
    public async Task A_path_that_is_not_a_local_daemon_binary_is_refused(string path) {
        var enrollment = Enrollment.Parse("laptop", path, null, null, out var refusal);

        await Assert.That(enrollment).IsNull();
        await Assert.That(refusal).Contains("kcap-daemon.exe");
    }

    [Test]
    [Arguments("")]
    [Arguments("a\"b")]
    [Arguments("a\\b")]
    [Arguments("a\nb")]
    public async Task A_name_that_could_break_the_command_line_is_refused(string name) {
        await Assert.That(Enrollment.Parse(name, Daemon, null, null, out _)).IsNull();
    }

    [Test]
    public async Task An_environment_file_must_be_a_local_path() {
        await Assert.That(Enrollment.Parse("laptop", Daemon, null, null, out _, @"C:\Users\alice\.config\kcap\daemon-service-laptop.env.json")!.EnvironmentFile)
            .IsEqualTo(@"C:\Users\alice\.config\kcap\daemon-service-laptop.env.json");
        await Assert.That(Enrollment.Parse("laptop", Daemon, null, null, out var refusal, @"\\server\env.json")).IsNull();
        await Assert.That(refusal).Contains("environment file");
    }

    [Test]
    public async Task A_max_agents_value_that_is_not_a_count_is_ignored() {
        await Assert.That(Enrollment.Parse("laptop", Daemon, null, "3", out _)!.MaxAgents).IsNull();
        await Assert.That(Enrollment.Parse("laptop", Daemon, null, -1, out _)!.MaxAgents).IsNull();
    }
}

using Capacitor.Cli.Service.Sessions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Service.Tests.Unit.Sessions;

/// Windows path semantics: the service runs nowhere else
[RunOn(OS.Windows)]
public class ServerEnrollmentTests {
    const string Root   = @"C:\Users\alice\.config\kcap\local";
    const string Server = @"C:\Users\alice\.config\kcap\local\bin\current\kcap-server.exe";
    const string Cli    = @"C:\Users\alice\AppData\Local\kcap\current\bin\kcap.exe";
    const string Log    = @"C:\Users\alice\.config\kcap\local\logs\server.log";
    const string Env    = @"C:\Users\alice\.config\kcap\local\service.env.json";

    [Test]
    public async Task Local_paths_to_the_server_and_cli_parse() {
        var enrollment = ServerEnrollment.Parse(Server, Root, Cli, Log, Env, 0, out var refusal);

        await Assert.That(refusal).IsNull();
        await Assert.That(enrollment).IsEqualTo(new ServerEnrollment(Server, Root, Cli, Log, Env));
        await Assert.That(enrollment!.Name).IsEqualTo("kcap-local");
    }

    [Test]
    public async Task The_stopped_flag_and_the_environment_file_may_be_absent() {
        var enrollment = ServerEnrollment.Parse(Server, Root, Cli, Log, null, null, out _);

        await Assert.That(enrollment).IsEqualTo(new ServerEnrollment(Server, Root, Cli, Log));
    }

    [Test]
    public async Task A_stopped_enrollment_is_refused_as_stopped() {
        var enrollment = ServerEnrollment.Parse(Server, Root, Cli, Log, Env, 1, out var refusal);

        await Assert.That(enrollment).IsNull();
        await Assert.That(refusal).IsEqualTo(ServerEnrollment.StoppedRefusal);
    }

    [Test]
    [Arguments(@"kcap\local\bin\current\kcap-server.exe")]
    [Arguments(@"\\server\share\kcap-server.exe")]
    [Arguments(@"C:\Users\alice\.config\kcap\local\bin\current\kcap-daemon.exe")]
    [Arguments("C:\\Users\\a\"b\\kcap-server.exe")]
    [Arguments("")]
    public async Task A_server_path_that_is_not_a_local_server_binary_is_refused(string path) {
        var enrollment = ServerEnrollment.Parse(path, Root, Cli, Log, null, null, out var refusal);

        await Assert.That(enrollment).IsNull();
        await Assert.That(refusal).Contains("kcap-server.exe");
    }

    [Test]
    [Arguments(@"kcap.exe")]
    [Arguments(@"\\server\share\kcap.exe")]
    [Arguments(@"C:\Users\alice\AppData\Local\kcap\current\bin\kcap-daemon.exe")]
    public async Task A_cli_path_that_is_not_a_local_cli_binary_is_refused(string path) {
        var enrollment = ServerEnrollment.Parse(Server, Root, path, Log, null, null, out var refusal);

        await Assert.That(enrollment).IsNull();
        await Assert.That(refusal).Contains("kcap.exe");
    }

    [Test]
    public async Task The_root_log_and_environment_file_must_be_local_paths() {
        await Assert.That(ServerEnrollment.Parse(Server, @"\\server\local", Cli, Log, null, null, out var root)).IsNull();
        await Assert.That(root).Contains("root");
        await Assert.That(ServerEnrollment.Parse(Server, Root, Cli, null, null, null, out var log)).IsNull();
        await Assert.That(log).Contains("log file");
        await Assert.That(ServerEnrollment.Parse(Server, Root, Cli, Log, @"\\server\env.json", null, out var env)).IsNull();
        await Assert.That(env).Contains("environment file");
    }
}

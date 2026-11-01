using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

sealed class FakeServerHost(Dictionary<int, SessionUser>? sessions = null) : FakeHost<ServerEnrollment>(
    sid => new ServerEnrollment(
        $@"C:\Users\{sid}\.config\kcap\local\bin\current\kcap-server.exe", $@"C:\Users\{sid}\.config\kcap\local",
        $@"C:\Users\{sid}\AppData\Local\kcap\current\bin\kcap.exe", $@"C:\Users\{sid}\.config\kcap\local\logs\server.log"),
    sessions);

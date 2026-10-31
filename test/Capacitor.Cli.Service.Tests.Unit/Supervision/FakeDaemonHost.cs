using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Tests.Unit.Supervision;

sealed class FakeDaemonHost() : FakeHost<Enrollment>(
    sid => new Enrollment("laptop", $@"C:\Users\{sid}\AppData\Local\kcap\current\kcap-daemon.exe", null, null));

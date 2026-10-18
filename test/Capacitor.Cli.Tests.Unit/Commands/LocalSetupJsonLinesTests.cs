using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.Commands;

internal sealed class LocalSetupJsonLinesTests {
    const string Id = "0123456789abcdef0123456789abcdef";

    public static IEnumerable<Func<(LocalInstallEvent Event, string Line)>> Events() {
        yield return () => (new LocalInstallEvent.PreflightPassed(PlanHint.Install, "1.3.0", "/r"),
            """{"v":1,"event":"preflight_passed","plan":"install","version":"1.3.0","root":"/r"}""");
        yield return () => (new LocalInstallEvent.PreflightPassed(PlanHint.Reuse, "1.3.0", "/r"),
            """{"v":1,"event":"preflight_passed","plan":"reuse","version":"1.3.0","root":"/r"}""");
        yield return () => (new LocalInstallEvent.Recovering("setup", "unit"),
            """{"v":1,"event":"recovering","operation":"setup","phase":"unit"}""");
        yield return () => (new LocalInstallEvent.Recovered("setup", "allowed"),
            """{"v":1,"event":"recovered","operation":"setup","phase":"allowed"}""");
        yield return () => (new LocalInstallEvent.PortChosen(9473),
            """{"v":1,"event":"port_chosen","port":9473}""");
        yield return () => (new LocalInstallEvent.Downloading("https://b.test/x.tar.gz", 123),
            """{"v":1,"event":"downloading","url":"https://b.test/x.tar.gz","bytes":123}""");
        yield return () => (new LocalInstallEvent.DownloadProgress(10, 123),
            """{"v":1,"event":"download_progress","received":10,"bytes":123}""");
        yield return () => (new LocalInstallEvent.Verified("ab12"),
            """{"v":1,"event":"verified","sha256":"ab12"}""");
        yield return () => (new LocalInstallEvent.Unpacked("/r/bin/1.3.0"),
            """{"v":1,"event":"unpacked","path":"/r/bin/1.3.0"}""");
        yield return () => (new LocalInstallEvent.Starting("io.kurrent.capacitor.local"),
            """{"v":1,"event":"starting","unit":"io.kurrent.capacitor.local"}""");
        yield return () => (new LocalInstallEvent.Ready("http://localhost:9473"),
            """{"v":1,"event":"ready","server_url":"http://localhost:9473"}""");
        yield return () => (new LocalInstallEvent.Committing(),
            """{"v":1,"event":"committing"}""");
        yield return () => (new LocalInstallEvent.Reused("http://localhost:9473", "1.2.0"),
            """{"v":1,"event":"reused","server_url":"http://localhost:9473","installed_version":"1.2.0"}""");
        yield return () => (new LocalInstallEvent.RollingBack("The server did not start."),
            """{"v":1,"event":"rolling_back","reason":"The server did not start."}""");
        yield return () => (new LocalInstallEvent.RolledBack(),
            """{"v":1,"event":"rolled_back"}""");
        yield return () => (new LocalInstallEvent.Committed("http://localhost:9473", Id, 9473, "1.3.0"),
            $$"""{"v":1,"event":"committed","server_url":"http://localhost:9473","installation_id":"{{Id}}","port":9473,"version":"1.3.0"}""");
    }

    [Test]
    [MethodDataSource(nameof(Events))]
    public async Task Each_event_renders_its_pinned_line(LocalInstallEvent e, string line) =>
        await Assert.That(LocalSetupJsonLines.Line(e)).IsEqualTo(line);

    [Test]
    public async Task A_failure_renders_its_code_and_message() =>
        await Assert.That(LocalSetupJsonLines.Failed(LocalInstallFailureCode.AnswerRequired, "Pass --single-user."))
                    .IsEqualTo("""{"v":1,"event":"failed","code":"answer_required","message":"Pass --single-user."}""");

    public static IEnumerable<Func<(Exception Error, bool SawCommitting, LocalInstallFailureCode Code)>> Failures() {
        yield return () => (new LocalInstallFailure(LocalInstallFailureCode.NotOurServer, "x"), false, LocalInstallFailureCode.NotOurServer);
        yield return () => (new LocalBundleException(LocalInstallFailureCode.VerificationFailed, "x"), false, LocalInstallFailureCode.VerificationFailed);
        yield return () => (new LocalLifecycleException("Another kcap lifecycle command is running."), true, LocalInstallFailureCode.LifecycleBusy);
        yield return () => (new LocalLifecycleException("The lease could not be taken."), false, LocalInstallFailureCode.StartFailed);
        yield return () => (new LocalLifecycleException("The lease could not be taken."), true, LocalInstallFailureCode.CommitFailed);
        yield return () => (new OperationCanceledException(), false, LocalInstallFailureCode.Cancelled);
        yield return () => (new InvalidOperationException("boom"), false, LocalInstallFailureCode.StartFailed);
    }

    [Test]
    [MethodDataSource(nameof(Failures))]
    public async Task An_escaping_exception_maps_to_its_code(Exception error, bool sawCommitting, LocalInstallFailureCode code) =>
        await Assert.That(LocalSetupJsonLines.Classify(error, sawCommitting).Code).IsEqualTo(code);
}

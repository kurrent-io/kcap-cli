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
        yield return () => (new LocalInstallEvent.Starting("io.kurrent.kcap.local"),
            """{"v":1,"event":"starting","unit":"io.kurrent.kcap.local"}""");
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
        yield return () => (new LocalInstallEvent.UpgradePlanned("1.2.0", "1.3.0"),
            """{"v":1,"event":"upgrade_planned","version":"1.3.0","from_version":"1.2.0"}""");
        yield return () => (new LocalInstallEvent.Stopping(), """{"v":1,"event":"stopping"}""");
        yield return () => (new LocalInstallEvent.Stopped(), """{"v":1,"event":"stopped"}""");
        yield return () => (new LocalInstallEvent.Switched("1.3.0"), """{"v":1,"event":"switched","version":"1.3.0"}""");
        yield return () => (new LocalInstallEvent.Restarting("1.2.0"), """{"v":1,"event":"restarting","version":"1.2.0"}""");
        yield return () => (new LocalInstallEvent.Upgraded("http://localhost:9473", "1.2.0", "1.3.0"),
            """{"v":1,"event":"upgraded","server_url":"http://localhost:9473","version":"1.3.0","from_version":"1.2.0"}""");
        yield return () => (new LocalInstallEvent.NothingToDo(UpgradeSkip.StackNewer, "1.2.0", "1.3.0"),
            """{"v":1,"event":"nothing_to_do","version":"1.2.0","installed_version":"1.3.0","reason":"stack_newer"}""");
        yield return () => (new LocalInstallEvent.NothingToDo(UpgradeSkip.NotInstalled, "1.3.0", null),
            """{"v":1,"event":"nothing_to_do","version":"1.3.0","reason":"not_installed"}""");
        yield return () => (new LocalInstallEvent.EmbeddingOllamaFound("http://localhost:11434", "0.12.3"),
            """{"v":1,"event":"embeddings_ollama_found","version":"0.12.3","url":"http://localhost:11434"}""");
        yield return () => (new LocalInstallEvent.EmbeddingModelPulling("mxbai-embed-large"),
            """{"v":1,"event":"embeddings_model_pulling","model":"mxbai-embed-large"}""");
        yield return () => (new LocalInstallEvent.EmbeddingModelProgress("mxbai-embed-large", 10, 670),
            """{"v":1,"event":"embeddings_model_progress","received":10,"bytes":670,"model":"mxbai-embed-large"}""");
        yield return () => (new LocalInstallEvent.EmbeddingModelReady("mxbai-embed-large", 1024),
            """{"v":1,"event":"embeddings_model_ready","model":"mxbai-embed-large","dimensions":1024}""");
        yield return () => (new LocalInstallEvent.EmbeddingsConfigured("mxbai-embed-large", null),
            """{"v":1,"event":"embeddings_configured","provider":"ollama","model":"mxbai-embed-large"}""");
        yield return () => (new LocalInstallEvent.EmbeddingsConfigured("nomic-embed-text", "mxbai-embed-large"),
            """{"v":1,"event":"embeddings_configured","provider":"ollama","model":"nomic-embed-text","replaced_model":"mxbai-embed-large"}""");
        yield return () => (new LocalInstallEvent.EmbeddingBuiltinDownloading(670),
            """{"v":1,"event":"embeddings_builtin_downloading","bytes":670}""");
        yield return () => (new LocalInstallEvent.EmbeddingBuiltinProgress(10, 670),
            """{"v":1,"event":"embeddings_builtin_progress","received":10,"bytes":670}""");
        yield return () => (new LocalInstallEvent.EmbeddingBuiltinConfigured(),
            """{"v":1,"event":"embeddings_configured","provider":"builtin"}""");
        yield return () => (new LocalInstallEvent.EmbeddingBuiltinConfigured("mxbai-embed-large"),
            """{"v":1,"event":"embeddings_configured","provider":"builtin","replaced_model":"mxbai-embed-large"}""");
        yield return () => (new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.BuiltinUnavailable, "No model."),
            """{"v":1,"event":"embeddings_skipped","code":"builtin_unavailable","message":"No model."}""");
        yield return () => (new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.ModelDownloadFailed, "Failed."),
            """{"v":1,"event":"embeddings_skipped","code":"model_download_failed","message":"Failed."}""");
        yield return () => (new LocalInstallEvent.EmbeddingsSkipped(LocalEmbeddingSkipCode.OllamaNotFound, "No Ollama."),
            """{"v":1,"event":"embeddings_skipped","code":"ollama_not_found","message":"No Ollama."}""");
        yield return () => (new LocalInstallEvent.WindowsServiceDeclined(), """{"v":1,"event":"windows_service_declined"}""");
        yield return () => (new LocalInstallEvent.WindowsServiceUnavailable("No service."),
            """{"v":1,"event":"windows_service_unavailable","message":"No service."}""");
        yield return () => (new LocalInstallEvent.Fetched("1.3.0", "/r/cache"), """{"v":1,"event":"fetched","version":"1.3.0","path":"/r/cache"}""");
    }

    [Test]
    [MethodDataSource(nameof(Events))]
    public async Task Each_event_renders_its_pinned_line(LocalInstallEvent e, string line) =>
        await Assert.That(LocalSetupJsonLines.Line(e)).IsEqualTo(line);

    [Test]
    public async Task A_failure_renders_its_code_and_message() =>
        await Assert.That(LocalSetupJsonLines.Failed(LocalInstallFailureCode.AnswerRequired, "Pass --single-user."))
                    .IsEqualTo("""{"v":1,"event":"failed","code":"answer_required","message":"Pass --single-user."}""");

    [Test]
    public async Task An_upgrade_failure_says_whether_the_new_version_is_selected() =>
        await Assert.That(LocalSetupJsonLines.Failed(LocalInstallFailureCode.StartFailed, "It did not start.", switched: true))
                    .IsEqualTo("""{"v":1,"event":"failed","code":"start_failed","message":"It did not start.","switched":true}""");

    public static IEnumerable<Func<(Exception Error, LocalInstallFailureCode Code)>> Failures() {
        yield return () => (new LocalInstallFailure(LocalInstallFailureCode.NotOurServer, "x"), LocalInstallFailureCode.NotOurServer);
        yield return () => (new LocalLifecycleException("The lease could not be taken."), LocalInstallFailureCode.StartFailed);
        yield return () => (new InvalidOperationException("boom"), LocalInstallFailureCode.StartFailed);
    }

    [Test]
    [MethodDataSource(nameof(Failures))]
    public async Task An_escaping_exception_maps_to_its_code(Exception error, LocalInstallFailureCode code) =>
        await Assert.That(LocalSetupJsonLines.Classify(error).Code).IsEqualTo(code);
}

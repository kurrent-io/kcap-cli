using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class OllamaClientTests {
    [Test]
    public async Task Nothing_listening_reads_as_no_ollama() {
        Uri gone;
        using (var server = WireMockServer.Start()) gone = new Uri(server.Url!);

        var version = await new OllamaClient(new HttpClient(), gone, TimeProvider.System).VersionAsync(CancellationToken.None);

        await Assert.That(version).IsNull();
    }

    [Test]
    public async Task A_server_that_is_not_ollama_reads_as_no_ollama() {
        using var ollama = new FakeOllama();

        await Assert.That(await ollama.Client().VersionAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task A_running_ollama_answers_its_version() {
        using var ollama = new FakeOllama().Running();

        await Assert.That(await ollama.Client().VersionAsync(CancellationToken.None)).IsEqualTo("0.12.3");
    }

    [Test]
    [Arguments("mxbai-embed-large:latest", "mxbai-embed-large", true)]
    [Arguments("mxbai-embed-large:latest", "mxbai-embed-large:latest", true)]
    [Arguments("mxbai-embed-large:335m", "mxbai-embed-large", false)]
    [Arguments("nomic-embed-text:latest", "mxbai-embed-large", false)]
    public async Task An_untagged_name_means_latest(string installed, string asked, bool has) {
        using var ollama = new FakeOllama().Running(installed);

        await Assert.That(await ollama.Client().HasModelAsync(asked, CancellationToken.None)).IsEqualTo(has);
    }

    [Test]
    public async Task A_pull_reports_bytes_over_every_layer_and_ends_on_success() {
        using var ollama = new FakeOllama().Pulls(
            """{"status":"pulling manifest"}""",
            """{"status":"pulling a","digest":"sha256:a","total":600,"completed":300}""",
            """{"status":"pulling b","digest":"sha256:b","total":400,"completed":0}""",
            """{"status":"pulling a","digest":"sha256:a","total":600,"completed":600}""",
            """{"status":"verifying sha256 digest"}""",
            """{"status":"success"}""");
        var progress = new RecordingProgress<(long Received, long Total)>();

        await ollama.Client().PullAsync("mxbai-embed-large", progress, CancellationToken.None);

        await Assert.That(progress.Reports).IsEquivalentTo([(300L, 600L), (300L, 1000L), (600L, 1000L)]);
        await Assert.That(ollama.LastBody("/api/pull")).IsEqualTo("""{"model":"mxbai-embed-large","stream":true}""");
    }

    [Test]
    public async Task A_pull_error_line_fails_with_ollamas_message() {
        using var ollama = new FakeOllama().Pulls("""{"status":"pulling manifest"}""", """{"error":"pull model manifest: file does not exist"}""");

        var ex = await Assert.ThrowsAsync<OllamaException>(
            () => ollama.Client().PullAsync("no-such-model", new RecordingProgress<(long, long)>(), CancellationToken.None));

        await Assert.That(ex!.Message).IsEqualTo("pull model manifest: file does not exist");
    }

    [Test]
    public async Task A_pull_that_ends_without_success_fails() {
        using var ollama = new FakeOllama().Pulls("""{"status":"pulling manifest"}""");

        await Assert.ThrowsAsync<OllamaException>(
            () => ollama.Client().PullAsync("mxbai-embed-large", new RecordingProgress<(long, long)>(), CancellationToken.None));
    }

    [Test]
    public async Task A_probe_embed_returns_the_vector_width() {
        using var ollama = new FakeOllama().Embeds(1024);

        await Assert.That(await ollama.Client().EmbedProbeAsync("mxbai-embed-large", CancellationToken.None)).IsEqualTo(1024);
        await Assert.That(ollama.LastBody("/api/embed")).IsEqualTo("""{"model":"mxbai-embed-large","input":["kcap"],"truncate":false}""");
    }

    [Test]
    public async Task A_model_that_cannot_embed_fails_with_ollamas_message() {
        using var ollama = new FakeOllama().CannotEmbed("llama3 does not support embeddings");

        var ex = await Assert.ThrowsAsync<OllamaException>(() => ollama.Client().EmbedProbeAsync("llama3", CancellationToken.None));

        await Assert.That(ex!.Message).IsEqualTo("llama3 does not support embeddings");
    }
}

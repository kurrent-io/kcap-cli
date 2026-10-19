using System.Runtime.Versioning;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using TUnit.Assertions.Enums;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

[ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
public class LocalEmbeddingStepTests {
    const string Model = OllamaClient.DefaultModel;

    [TempDir] public required TempDir Tmp { get; init; }

    readonly RecordingProgress<LocalInstallEvent> _events = new();

    LocalRoot Root => field ??= new(Tmp.CreateDir("local"));

    Task<bool> Run(FakeOllama ollama, LocalEmbeddingAnswer answer) =>
        LocalEmbeddingStep.RunAsync(Root, answer, ollama.Client(), _events, CancellationToken.None);

    static readonly LocalEmbeddingAnswer UseOllama = new LocalEmbeddingAnswer.Ollama(null);

    T Single<T>() where T : LocalInstallEvent => _events.Reports.OfType<T>().Single();

    [Test]
    public async Task Without_ollama_the_step_skips_and_writes_nothing() {
        using var ollama = new FakeOllama();

        var changed = await Run(ollama, UseOllama);

        await Assert.That(changed).IsFalse();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.OllamaNotFound);
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Message).EndsWith("search works by keyword.");
        await Assert.That(File.Exists(LocalEmbeddingFile.PathFor(Root))).IsFalse();
    }

    [Test]
    public async Task A_missing_model_is_pulled_probed_and_recorded() {
        using var ollama = new FakeOllama().Running().Pulls(
            """{"status":"pulling a","digest":"sha256:a","total":200,"completed":100}""",
            """{"status":"pulling a","digest":"sha256:a","total":200,"completed":200}""",
            """{"status":"success"}""").Embeds(1024);

        var changed = await Run(ollama, UseOllama);

        await Assert.That(changed).IsTrue();
        await Assert.That(_events.Reports.Select(e => e.GetType().Name)).IsEquivalentTo([
            nameof(LocalInstallEvent.EmbeddingOllamaFound),
            nameof(LocalInstallEvent.EmbeddingModelPulling),
            nameof(LocalInstallEvent.EmbeddingModelProgress),
            nameof(LocalInstallEvent.EmbeddingModelProgress),
            nameof(LocalInstallEvent.EmbeddingModelReady),
            nameof(LocalInstallEvent.EmbeddingsConfigured),
        ], CollectionOrdering.Matching);
        await Assert.That(Single<LocalInstallEvent.EmbeddingModelReady>()).IsEqualTo(new LocalInstallEvent.EmbeddingModelReady(Model, 1024));
        await Assert.That(Single<LocalInstallEvent.EmbeddingsConfigured>()).IsEqualTo(new LocalInstallEvent.EmbeddingsConfigured(Model, null));
        await Assert.That(LocalEmbeddingFile.ReadModel(Root)).IsEqualTo(Model);
        await Assert.That(File.GetUnixFileMode(LocalEmbeddingFile.PathFor(Root))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(LocalEmbeddingFile.PathFor(Root))!))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task A_model_ollama_has_is_not_pulled_and_an_unchanged_one_needs_no_restart() {
        using var ollama = new FakeOllama().Running($"{Model}:latest").Embeds(1024);
        LocalEmbeddingFile.Write(Root, Model);

        var changed = await Run(ollama, UseOllama);

        await Assert.That(changed).IsFalse();
        await Assert.That(ollama.Requests("/api/pull")).IsEqualTo(0);
        await Assert.That(Single<LocalInstallEvent.EmbeddingsConfigured>()).IsEqualTo(new LocalInstallEvent.EmbeddingsConfigured(Model, null));
    }

    [Test]
    public async Task No_model_named_keeps_the_configured_one() {
        using var ollama = new FakeOllama().Running("nomic-embed-text:latest").Embeds(768);
        LocalEmbeddingFile.Write(Root, "nomic-embed-text");

        await Run(ollama, UseOllama);

        await Assert.That(Single<LocalInstallEvent.EmbeddingsConfigured>().Model).IsEqualTo("nomic-embed-text");
        await Assert.That(ollama.Requests("/api/pull")).IsEqualTo(0);
    }

    [Test]
    public async Task A_named_model_replaces_the_configured_one_and_says_which() {
        using var ollama = new FakeOllama().Running("nomic-embed-text:latest").Embeds(768);
        LocalEmbeddingFile.Write(Root, Model);

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Ollama("nomic-embed-text"));

        await Assert.That(changed).IsTrue();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsConfigured>()).IsEqualTo(new LocalInstallEvent.EmbeddingsConfigured("nomic-embed-text", Model));
        await Assert.That(LocalEmbeddingFile.ReadModel(Root)).IsEqualTo("nomic-embed-text");
    }

    [Test]
    public async Task A_failed_pull_skips_and_keeps_what_was_configured() {
        using var ollama = new FakeOllama().Running().Pulls("""{"error":"pull model manifest: file does not exist"}""");
        LocalEmbeddingFile.Write(Root, "nomic-embed-text");

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Ollama("no-such-model"));

        await Assert.That(changed).IsFalse();
        var skipped = Single<LocalInstallEvent.EmbeddingsSkipped>();
        await Assert.That(skipped.Code).IsEqualTo(LocalEmbeddingSkipCode.PullFailed);
        await Assert.That(skipped.Message).Contains("file does not exist");
        await Assert.That(skipped.Message).Contains("keeps embedding with nomic-embed-text");
        await Assert.That(LocalEmbeddingFile.ReadModel(Root)).IsEqualTo("nomic-embed-text");
    }

    [Test]
    public async Task A_model_that_cannot_embed_is_not_recorded() {
        using var ollama = new FakeOllama().Running("llama3:latest").CannotEmbed("this model does not support embeddings");

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Ollama("llama3"));

        await Assert.That(changed).IsFalse();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.ModelUnusable);
        await Assert.That(File.Exists(LocalEmbeddingFile.PathFor(Root))).IsFalse();
    }

    [Test]
    public async Task Declining_removes_a_configured_model_without_asking_ollama() {
        using var ollama = new FakeOllama().Running();
        LocalEmbeddingFile.Write(Root, Model);

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Skip());

        await Assert.That(changed).IsTrue();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.Declined);
        await Assert.That(File.Exists(LocalEmbeddingFile.PathFor(Root))).IsFalse();
        await Assert.That(ollama.Requests("/api/version")).IsEqualTo(0);
    }

    [Test]
    public async Task Declining_with_nothing_configured_changes_nothing() {
        using var ollama = new FakeOllama();

        await Assert.That(await Run(ollama, new LocalEmbeddingAnswer.Skip())).IsFalse();
    }

    [Test]
    public async Task A_pull_reports_progress_once_per_percent() {
        using var ollama = new FakeOllama().Running().Pulls(
            """{"status":"pulling a","digest":"sha256:a","total":1000,"completed":1}""",
            """{"status":"pulling a","digest":"sha256:a","total":1000,"completed":2}""",
            """{"status":"pulling a","digest":"sha256:a","total":1000,"completed":10}""",
            """{"status":"success"}""").Embeds(4);

        await Run(ollama, UseOllama);

        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingModelProgress>().Select(p => p.Received)).IsEquivalentTo([1L, 10L]);
    }
}

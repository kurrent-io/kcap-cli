using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Capacitor.Cli.Core.Http;
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

    static readonly byte[] Onnx  = RandomNumberGenerator.GetBytes(3_000);
    static readonly byte[] Vocab = RandomNumberGenerator.GetBytes(500);

    static readonly LocalEmbeddingModelManifest Manifest = new("mxbai-embed-large-v1", 1, new('b', 64), [
        new("model.onnx", Convert.ToHexStringLower(SHA256.HashData(Onnx)), Onnx.Length),
        new("vocab.txt", Convert.ToHexStringLower(SHA256.HashData(Vocab)), Vocab.Length),
    ]);

    readonly Publication _models = new();

    LocalModelSetDownloader Downloader =>
        new(Root, new LocalBundleClient(new HttpClient(new ModelFiles(_models)), TimeProvider.System) { BaseUrl = "https://bundles.test/local" });

    Task<LocalEmbeddingSetting?> Decide(FakeOllama ollama, LocalEmbeddingAnswer answer, LocalEmbeddingModelManifest? manifest = null) =>
        LocalEmbeddingStep.DecideAsync(Root, answer, ollama.Client(), Downloader, manifest, _events, CancellationToken.None);

    /// <returns>Whether the recorded setting changed.</returns>
    async Task<bool> Run(FakeOllama ollama, LocalEmbeddingAnswer answer, LocalEmbeddingModelManifest? manifest = null) {
        var previous = LocalEmbeddingFile.Read(Root);
        var setting  = await Decide(ollama, answer, manifest);
        if (setting is not null && LocalEmbeddingStep.Record(Root, setting, _events)) LocalEmbeddingStep.Applied(previous, setting, _events);

        return setting is not null;
    }

    public enum Recorded { Nothing, Declined, Ollama, Builtin, Unknown }

    public enum Given { Default, Builtin, Ollama, Skip }

    public enum Decided { Stands, Ollama, Builtin, Declined }

    void Write(Recorded recorded) {
        switch (recorded) {
            case Recorded.Declined: LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Declined); break;
            case Recorded.Ollama:   LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama(Model)); break;
            case Recorded.Builtin:  LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Builtin(Manifest.ModelSet)); break;
            case Recorded.Unknown:
                Directory.CreateDirectory(Path.GetDirectoryName(LocalEmbeddingFile.PathFor(Root))!);
                File.WriteAllText(LocalEmbeddingFile.PathFor(Root), """{"provider":"cohere"}""");
                break;
        }
    }

    void PlaceModelSet() {
        var dir = Path.Combine(Root.Directory, "models", Manifest.ModelSet);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model.onnx"), Onnx);
        File.WriteAllBytes(Path.Combine(dir, "vocab.txt"), Vocab);
    }

    static LocalEmbeddingAnswer Answer(Given given) => given switch {
        Given.Default => new LocalEmbeddingAnswer.Default(),
        Given.Builtin => new LocalEmbeddingAnswer.Builtin(),
        Given.Ollama  => new LocalEmbeddingAnswer.Ollama(Model),
        _             => new LocalEmbeddingAnswer.Skip(),
    };

    static LocalEmbeddingSetting? Expected(Decided decided) => decided switch {
        Decided.Ollama   => LocalEmbeddingSetting.Ollama(Model),
        Decided.Builtin  => LocalEmbeddingSetting.Builtin(Manifest.ModelSet),
        Decided.Declined => LocalEmbeddingSetting.Declined,
        _                => null,
    };

    /// <summary>What the step decides from the recorded choice, the answer, whether Ollama answers, whether a manifest
    /// offers the built-in model and whether that model is already in place; and whether it downloaded the model.</summary>
    [Test]
    [Arguments(Recorded.Nothing,  Given.Default, true,  true,  false, Decided.Ollama,   false, null)]
    [Arguments(Recorded.Nothing,  Given.Default, false, true,  false, Decided.Builtin,  true,  null)]
    [Arguments(Recorded.Nothing,  Given.Default, false, false, false, Decided.Stands,   false, "ollama_not_found")]
    [Arguments(Recorded.Declined, Given.Default, false, true,  false, Decided.Stands,   false, "declined")]
    [Arguments(Recorded.Builtin,  Given.Default, false, true,  false, Decided.Stands,   true,  null)]
    [Arguments(Recorded.Builtin,  Given.Default, true,  true,  true,  Decided.Stands,   false, null)]
    [Arguments(Recorded.Builtin,  Given.Default, false, false, false, Decided.Stands,   false, "builtin_unavailable")]
    [Arguments(Recorded.Builtin,  Given.Builtin, false, true,  false, Decided.Stands,   true,  null)]
    [Arguments(Recorded.Builtin,  Given.Skip,    false, true,  true,  Decided.Declined, false, "declined")]
    [Arguments(Recorded.Builtin,  Given.Ollama,  true,  true,  true,  Decided.Ollama,   false, null)]
    [Arguments(Recorded.Ollama,   Given.Default, false, true,  false, Decided.Stands,   false, "ollama_not_found")]
    [Arguments(Recorded.Unknown,  Given.Default, true,  true,  false, Decided.Stands,   false, "unrecognised_provider")]
    [Arguments(Recorded.Unknown,  Given.Builtin, false, true,  false, Decided.Stands,   false, "unrecognised_provider")]
    [Arguments(Recorded.Unknown,  Given.Ollama,  true,  true,  false, Decided.Stands,   false, "unrecognised_provider")]
    [Arguments(Recorded.Unknown,  Given.Skip,    false, true,  false, Decided.Stands,   false, "unrecognised_provider")]
    [Arguments(Recorded.Nothing,  Given.Builtin, true,  false, false, Decided.Stands,   false, "builtin_unavailable")]
    [Arguments(Recorded.Nothing,  Given.Builtin, true,  true,  false, Decided.Builtin,  true,  null)]
    [Arguments(Recorded.Declined, Given.Builtin, false, true,  true,  Decided.Builtin,  false, null)]
    public async Task The_decision_follows_the_record_the_answer_and_what_is_available(
        Recorded recorded, Given given, bool ollamaUp, bool offered, bool present, Decided decided, bool downloads, string? skip) {
        Write(recorded);
        if (present) PlaceModelSet();
        using var ollama = ollamaUp ? new FakeOllama().Running($"{Model}:latest").Embeds(1024) : new FakeOllama();

        var setting = await Decide(ollama, Answer(given), offered ? Manifest : null);

        await Assert.That(setting).IsEqualTo(Expected(decided));
        await Assert.That(_models.Requests > 0).IsEqualTo(downloads);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsSkipped>().Select(k => k.Code.Wire()).SingleOrDefault()).IsEqualTo(skip);
        if (downloads) await Assert.That(Downloader.IsPresent(Manifest)).IsTrue();
    }

    [Test]
    public async Task A_builtin_download_reports_its_size_and_progress_then_the_configured_model() {
        using var ollama = new FakeOllama();

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Builtin(), Manifest);

        await Assert.That(changed).IsTrue();
        await Assert.That(Single<LocalInstallEvent.EmbeddingBuiltinDownloading>()).IsEqualTo(new LocalInstallEvent.EmbeddingBuiltinDownloading(Manifest.TotalSize));
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingBuiltinProgress>().Last())
            .IsEqualTo(new LocalInstallEvent.EmbeddingBuiltinProgress(Manifest.TotalSize, Manifest.TotalSize));
        await Assert.That(_events.Reports.Last()).IsEqualTo(new LocalInstallEvent.EmbeddingBuiltinConfigured());
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Builtin(Manifest.ModelSet));
    }

    [Test]
    public async Task A_failed_builtin_download_skips_and_records_nothing() {
        using var ollama = new FakeOllama();
        _models.Missing = true;

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Builtin(), Manifest);

        await Assert.That(changed).IsFalse();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.ModelDownloadFailed);
        await Assert.That(File.Exists(LocalEmbeddingFile.PathFor(Root))).IsFalse();
    }

    [Test]
    public async Task A_recorded_builtin_model_in_place_is_reported_configured() {
        using var ollama = new FakeOllama();
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Builtin(Manifest.ModelSet));
        PlaceModelSet();

        await Run(ollama, new LocalEmbeddingAnswer.Default(), Manifest);

        await Assert.That(Single<LocalInstallEvent.EmbeddingBuiltinConfigured>()).IsNotNull();
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingBuiltinDownloading>()).IsEmpty();
    }

    /// <summary>A publication of <see cref="Manifest"/>'s files; <see cref="Missing"/> answers 404 for every one.</summary>
    sealed class Publication {
        public bool Missing { get; set; }

        public int Requests { get; set; }
    }

    sealed class ModelFiles(Publication publication) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            publication.Requests++;
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("model.onnx", StringComparison.Ordinal) ? Onnx : path.EndsWith("vocab.txt", StringComparison.Ordinal) ? Vocab : null;

            return Task.FromResult(publication.Missing || body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    static readonly LocalEmbeddingAnswer UseOllama = new LocalEmbeddingAnswer.Ollama(null);

    T Single<T>() where T : LocalInstallEvent => _events.Reports.OfType<T>().Single();

    [Test]
    public async Task Without_ollama_the_step_skips_and_writes_nothing() {
        using var ollama = new FakeOllama();

        var changed = await Run(ollama, UseOllama);

        await Assert.That(changed).IsFalse();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.OllamaNotFound);
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Message).EndsWith(
            "; search works by keyword. Start Ollama, then run kcap setup --local again to turn on search by meaning.");
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
        await Assert.That(LocalEmbeddingFile.Read(Root)?.Model).IsEqualTo(Model);
        await Assert.That(File.GetUnixFileMode(LocalEmbeddingFile.PathFor(Root))).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(File.GetUnixFileMode(Path.GetDirectoryName(LocalEmbeddingFile.PathFor(Root))!))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task A_model_ollama_has_is_not_pulled_and_an_unchanged_one_needs_no_restart() {
        using var ollama = new FakeOllama().Running($"{Model}:latest").Embeds(1024);
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama(Model));

        var changed = await Run(ollama, UseOllama);

        await Assert.That(changed).IsFalse();
        await Assert.That(ollama.Requests("/api/pull")).IsEqualTo(0);
        await Assert.That(Single<LocalInstallEvent.EmbeddingsConfigured>()).IsEqualTo(new LocalInstallEvent.EmbeddingsConfigured(Model, null));
    }

    [Test]
    public async Task No_model_named_keeps_the_configured_one() {
        using var ollama = new FakeOllama().Running("nomic-embed-text:latest").Embeds(768);
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama("nomic-embed-text"));

        await Run(ollama, UseOllama);

        await Assert.That(Single<LocalInstallEvent.EmbeddingsConfigured>().Model).IsEqualTo("nomic-embed-text");
        await Assert.That(ollama.Requests("/api/pull")).IsEqualTo(0);
    }

    [Test]
    public async Task A_named_model_replaces_the_configured_one_and_says_which() {
        using var ollama = new FakeOllama().Running("nomic-embed-text:latest").Embeds(768);
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama(Model));

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Ollama("nomic-embed-text"));

        await Assert.That(changed).IsTrue();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsConfigured>()).IsEqualTo(new LocalInstallEvent.EmbeddingsConfigured("nomic-embed-text", Model));
        await Assert.That(LocalEmbeddingFile.Read(Root)?.Model).IsEqualTo("nomic-embed-text");
    }

    [Test]
    public async Task A_failed_pull_skips_and_keeps_what_was_configured() {
        using var ollama = new FakeOllama().Running().Pulls("""{"error":"pull model manifest: file does not exist"}""");
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama("nomic-embed-text"));

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Ollama("no-such-model"));

        await Assert.That(changed).IsFalse();
        var skipped = Single<LocalInstallEvent.EmbeddingsSkipped>();
        await Assert.That(skipped.Code).IsEqualTo(LocalEmbeddingSkipCode.PullFailed);
        await Assert.That(skipped.Message).Contains("file does not exist");
        await Assert.That(skipped.Message).Contains("keeps embedding with nomic-embed-text");
        await Assert.That(LocalEmbeddingFile.Read(Root)?.Model).IsEqualTo("nomic-embed-text");
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
    public async Task Declining_replaces_a_configured_model_without_asking_ollama() {
        using var ollama = new FakeOllama().Running();
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama(Model));

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Skip());

        await Assert.That(changed).IsTrue();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.Declined);
        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(LocalEmbeddingSetting.Declined);
        await Assert.That(ollama.Requests("/api/version")).IsEqualTo(0);
    }

    [Test]
    public async Task A_decline_outlives_a_later_run_that_names_no_model() {
        using var ollama = new FakeOllama().Running($"{Model}:latest").Embeds(1024);
        await Run(ollama, new LocalEmbeddingAnswer.Skip());
        _events.Reports.Clear();

        var changed = await Run(ollama, UseOllama);

        await Assert.That(changed).IsFalse();
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.Declined);
        await Assert.That(ollama.Requests("/api/version")).IsEqualTo(0);
    }

    [Test]
    public async Task A_named_model_overrides_a_decline() {
        using var ollama = new FakeOllama().Running($"{Model}:latest").Embeds(1024);
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Declined);

        var changed = await Run(ollama, new LocalEmbeddingAnswer.Ollama(Model));

        await Assert.That(changed).IsTrue();
        await Assert.That(LocalEmbeddingFile.Read(Root)?.Model).IsEqualTo(Model);
    }

    [Test]
    public async Task Declining_twice_records_nothing_new() {
        using var ollama = new FakeOllama();
        await Assert.That(await Run(ollama, new LocalEmbeddingAnswer.Skip())).IsTrue();

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

    [Test]
    public async Task An_unrecognised_recorded_provider_is_kept_and_reported() {
        var path = LocalEmbeddingFile.PathFor(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"provider":"cohere"}""");
        using var ollama = new FakeOllama().Running($"{Model}:latest").Embeds(1024);

        var changed = await Run(ollama, UseOllama);

        await Assert.That(changed).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo("""{"provider":"cohere"}""");
        await Assert.That(Single<LocalInstallEvent.EmbeddingsSkipped>().Code).IsEqualTo(LocalEmbeddingSkipCode.UnrecognisedProvider);
        await Assert.That(_events.Reports.OfType<LocalInstallEvent.EmbeddingsConfigured>()).IsEmpty();
    }
}

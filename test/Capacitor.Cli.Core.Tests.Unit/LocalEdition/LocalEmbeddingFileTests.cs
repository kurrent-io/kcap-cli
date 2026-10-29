using System.Text.Json;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalEmbeddingFileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= new(Tmp.PathTo("local"));

    void Record(string text) {
        var path = LocalEmbeddingFile.PathFor(Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Test]
    [Arguments("""{"provider":"ollama","model":"m"}""", LocalEmbeddingProvider.Ollama, "m", null, false)]
    [Arguments("""{"provider":"ollama","model":"m","pending":true}""", LocalEmbeddingProvider.Ollama, "m", null, true)]
    [Arguments("""{"provider":"none"}""", LocalEmbeddingProvider.None, null, null, false)]
    [Arguments("""{"provider":"none","builtin":{"model_set":"abc","pending":true}}""", LocalEmbeddingProvider.Builtin, null, "abc", true)]
    [Arguments("""{"provider":"none","builtin":{"model_set":"abc"}}""", LocalEmbeddingProvider.Builtin, null, "abc", false)]
    [Arguments("""{"provider":"cohere"}""", LocalEmbeddingProvider.Unknown, null, null, false)]
    public async Task Reads_the_recorded_setting(string text, LocalEmbeddingProvider provider, string? model, string? set, bool pending) {
        Record(text);

        var setting = LocalEmbeddingFile.Read(Root);

        await Assert.That(setting).IsNotNull();
        await Assert.That(setting!.Provider).IsEqualTo(provider);
        await Assert.That(setting.Model).IsEqualTo(model);
        await Assert.That(setting.ModelSetId).IsEqualTo(set);
        await Assert.That(setting.Pending).IsEqualTo(pending);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("""{"provider":"ollama"}""")]
    public async Task Garbage_reads_as_no_setting(string text) {
        Record(text);

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsNull();
    }

    [Test]
    public async Task Builtin_round_trips_with_its_own_pending_flag() {
        var setting = LocalEmbeddingSetting.Builtin("abc") with { Pending = true };

        await Assert.That(LocalEmbeddingFile.Write(Root, setting)).IsTrue();

        await Assert.That(LocalEmbeddingFile.Read(Root)).IsEqualTo(setting);
        using var json = JsonDocument.Parse(File.ReadAllBytes(LocalEmbeddingFile.PathFor(Root)));
        await Assert.That(json.RootElement.TryGetProperty("pending", out var top) && top.GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Old_reader_sees_a_non_pending_decline() {
        LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Builtin("abc") with { Pending = true });

        var seen = OldReader.Read(File.ReadAllBytes(LocalEmbeddingFile.PathFor(Root)));

        await Assert.That(seen).IsEqualTo("declined:pending=False");
    }

    [Test]
    public async Task Write_refuses_to_overwrite_an_unknown_provider() {
        Record("""{"provider":"cohere"}""");

        await Assert.That(LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Ollama("m"))).IsFalse();

        await Assert.That(File.ReadAllText(LocalEmbeddingFile.PathFor(Root))).IsEqualTo("""{"provider":"cohere"}""");
    }

    [Test]
    public async Task Write_refuses_an_unknown_setting() {
        await Assert.That(LocalEmbeddingFile.Write(Root, LocalEmbeddingSetting.Unknown)).IsFalse();

        await Assert.That(File.Exists(LocalEmbeddingFile.PathFor(Root))).IsFalse();
    }

    // A private copy of the Wire class and Read switch of the CLI that predates the built-in provider.
    static class OldReader {
        static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        sealed class Wire {
            public string? Provider { get; set; }
            public string? Model { get; set; }
            public bool Pending { get; set; }
        }

        public static string Read(byte[] bytes) {
            return JsonSerializer.Deserialize<Wire>(bytes, Options) switch {
                { Provider: "ollama", Model: { Length: > 0 } model } wire => $"ollama:{model}:pending={wire.Pending}",
                { Provider: "none" } wire                                 => $"declined:pending={wire.Pending}",
                _                                                         => "none",
            };
        }
    }
}

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Tests.Unit.Config;

public class ExperimentalSettingsStoreTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Lazy: injection happens after construction.
    ExperimentalSettingsStore Store => field ??= new ExperimentalSettingsStore(Config.Root);

    string FilePath => Config.PathTo(ExperimentalSettingsStore.FileName);

    [Test]
    public async Task Missing_file_reads_every_feature_off() {
        var read = Store.Load("default");

        await Assert.That(read.Readable).IsTrue();
        await Assert.That(read.IsEnabled("codex-app-server")).IsFalse();
    }


    [Test]
    public async Task Set_then_load_round_trips_per_profile() {
        await Assert.That(await Store.SetAsync("default", "codex-app-server", true, CancellationToken.None)).IsTrue();

        await Assert.That(Store.Load("default").IsEnabled("codex-app-server")).IsTrue();
        await Assert.That(Store.Load("work").IsEnabled("codex-app-server")).IsFalse();
    }

    /// <summary>The desktop switch saves from the UI thread, so waiting out another writer's lock must
    /// happen after <c>SetAsync</c> has returned its task.</summary>
    [Test]
    public async Task Set_waits_for_the_lock_off_the_calling_thread() {
        Task<bool> set;
        using (Config.Root.AcquireLock(ExperimentalSettingsStore.FileName)) {
            set = Store.SetAsync("default", "codex-app-server", true, CancellationToken.None);
            await Task.Delay(100);
            await Assert.That(set.IsCompleted).IsFalse();
        }

        await Assert.That(await set).IsTrue();
        await Assert.That(Store.Load("default").IsEnabled("codex-app-server")).IsTrue();
    }

    [Test]
    public async Task Set_preserves_unknown_profiles_and_ids() {
        await File.WriteAllTextAsync(FilePath, """{"work":{"future-x":true},"default":{"future-y":false}}""");

        await Store.SetAsync("default", "codex-app-server", true, CancellationToken.None);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsObject();
        await Assert.That((bool)root["work"]!["future-x"]!).IsTrue();
        await Assert.That((bool)root["default"]!["future-y"]!).IsFalse();
        await Assert.That((bool)root["default"]!["codex-app-server"]!).IsTrue();
    }

    [Test]
    [Arguments("""{"default":""")]
    [Arguments("")]
    [Arguments("  \n")]
    [Arguments("""{"default":{"codex-app-server":true},"default":{}}""")]
    [Arguments("""{"default":{"codex-app-server":true,"codex-app-server":false}}""")]
    public async Task Unreadable_file_reads_off_with_a_problem_and_is_never_overwritten(string content) {
        await File.WriteAllTextAsync(FilePath, content);
        var before = await File.ReadAllBytesAsync(FilePath);

        var read = Store.Load("default");
        var set = await Store.SetAsync("default", "codex-app-server", true, CancellationToken.None);

        await Assert.That(read.Readable).IsFalse();
        await Assert.That(read.Problem).IsNotNull();
        await Assert.That(read.IsEnabled("codex-app-server")).IsFalse();
        await Assert.That(set).IsFalse();
        await Assert.That(await File.ReadAllBytesAsync(FilePath)).IsEquivalentTo(before);
    }

    [Test]
    public async Task Non_boolean_value_reads_as_off() {
        await File.WriteAllTextAsync(FilePath, """{"default":{"codex-app-server":"yes"}}""");

        var read = Store.Load("default");

        await Assert.That(read.Readable).IsTrue();
        await Assert.That(read.IsEnabled("codex-app-server")).IsFalse();
    }

    [Test]
    public async Task Find_is_case_sensitive() {
        await Assert.That(ExperimentalFeatures.Find("codex-app-server")).IsEqualTo(ExperimentalFeatures.CodexAppServer);
        await Assert.That(ExperimentalFeatures.Find("Codex-App-Server")).IsNull();
        await Assert.That(ExperimentalFeatures.All).Contains(ExperimentalFeatures.CodexAppServer);
    }
}

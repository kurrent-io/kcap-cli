using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LocalSearchViewModelTests {
    const string Reused = "{\"v\":1,\"event\":\"reused\",\"server_url\":\"http://localhost:5000\",\"version\":\"1.2.0\"}";

    sealed class Harness : IDisposable {
        public readonly FakeKcapCli Cli = new();
        public readonly BehaviorSubject<ServerLaneStatus> Status = new(new(ServerLaneState.Connected));
        public readonly LocalSearchController Search;
        public readonly LocalUpgradeController Upgrade;
        public readonly LocalSearchViewModel Vm;
        public int Reconnects;
        /// What `data/embedding.json` holds; the view model reads it again once a run settles.
        public LocalEmbeddingSetting? Setting;
        /// What the installed stack's manifest offers; looked up again on every reconnect.
        public LocalEmbeddingModelManifest? Offered;
        /// Set before construction to hold the lookup open.
        public TaskCompletionSource<LocalEmbeddingModelManifest?>? Lookup;

        public int SettingsOpened;
        public readonly BehaviorSubject<Action?> OpenSettings = new(null);

        public Harness(LocalEmbeddingSetting? setting, bool offered = true, TaskCompletionSource<LocalEmbeddingModelManifest?>? lookup = null) {
            Lookup  = lookup;
            Setting = setting;
            Offered = offered ? new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]) : null;
            Search  = new LocalSearchController(() => Cli, action => action(), held: () => Upgrade.Running);
            Upgrade = new LocalUpgradeController(() => Cli, action => action(), held: () => Search.Running);
            Vm = new LocalSearchViewModel(() => Setting, _ => Lookup?.Task ?? Task.FromResult(Offered), Status, Search, Upgrade, () => {
                Reconnects++;
                return Task.CompletedTask;
            }, CancellationToken.None, ImmediateScheduler.Instance, openSettings: OpenSettings);
        }

        public async Task ApplyAsync(int exitCode, params string[] lines) {
            Cli.SetupLocalBehavior = LocalSetupScript.Prints(exitCode, lines);
            await Vm.ApplyCommand.Execute().ToTask();
            await Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Vm.Dispose();
    }

    [Test]
    public async Task The_rail_names_how_the_server_searches() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var keyword = new Harness(null);
            using var meaning = new Harness(LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

            await Assert.That(keyword.Vm.StateText).IsEqualTo("keyword search");
            await Assert.That(meaning.Vm.StateText).IsEqualTo("search by meaning");
            await Assert.That(meaning.Vm.CurrentText).IsEqualTo("Searches by meaning with mxbai-embed-large.");
        });
    }

    /// <summary>The rail's search word is the way into the Settings tab that changes it.</summary>
    [Test]
    public async Task The_rail_word_opens_search_settings() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            h.OpenSettings.OnNext(() => h.SettingsOpened++);

            await h.Vm.OpenSettingsCommand.Execute().ToTask();

            await Assert.That(h.SettingsOpened).IsEqualTo(1);
        });
    }

    /// <summary>A local server reached by URL with no profile has no Settings window, so the rail word opens the
    /// controls in place instead of linking to a window that cannot open.</summary>
    [Test]
    public async Task Without_a_settings_window_the_rail_word_opens_the_controls_in_place() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);

            await Assert.That(await h.Vm.OpenSettingsCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(h.Vm.ShowsInlineSettings).IsTrue();
            await Assert.That(h.Vm.ShowsSettingsLink).IsFalse();

            h.OpenSettings.OnNext(() => h.SettingsOpened++);

            await Assert.That(await h.Vm.OpenSettingsCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(h.Vm.ShowsInlineSettings).IsFalse();
            await Assert.That(h.Vm.ShowsSettingsLink).IsTrue();
        });
    }

    [Test]
    public async Task The_builtin_option_follows_the_installed_stack_and_is_looked_up_again_on_reconnect() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null, offered: false);

            await Assert.That(h.Vm.Choice.BuiltinEnabled).IsFalse();
            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(h.Vm.Choice.Choice.Args).IsEmpty();

            h.Offered = new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]);
            h.Status.OnNext(new(ServerLaneState.Retrying));
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.Choice.BuiltinEnabled).IsTrue();
            await Assert.That(h.Vm.Choice.UseBuiltin).IsTrue();
        });
    }

    [Test]
    public async Task Builtin_cannot_be_applied_as_a_new_choice_while_the_lookup_is_pending() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var lookup = new TaskCompletionSource<LocalEmbeddingModelManifest?>();
            using var h = new Harness(LocalEmbeddingSetting.Declined, lookup: lookup);

            h.Vm.Choice.UseBuiltin = true;

            await Assert.That(h.Vm.Choice.BuiltinEnabled).IsFalse();
            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(h.Vm.CanApply).IsFalse();

            var offered = h.Vm.Choice.WhenAnyValue(x => x.BuiltinEnabled).Where(enabled => enabled).FirstAsync().ToTask();
            lookup.SetResult(h.Offered);
            await offered.WaitAsync(TimeSpan.FromSeconds(5));
            h.Vm.Choice.UseBuiltin = true;

            await Assert.That(h.Vm.Choice.Choice.Args).IsEquivalentTo(["--builtin-embeddings"]);
            await Assert.That(h.Vm.CanApply).IsTrue();

            using var recorded = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)), lookup: new());
            await Assert.That(recorded.Vm.Choice.BuiltinEnabled).IsTrue();
            await Assert.That(recorded.Vm.Choice.UseBuiltin).IsTrue();
        });
    }

    /// While the lookup is open the shown recommendation is keyword search, yet setup left to decide could pick the
    /// built-in model, so nothing applies until the recommendation is the one setup will reach.
    [Test]
    public async Task An_unchosen_recommendation_waits_for_the_builtin_lookup() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var lookup = new TaskCompletionSource<LocalEmbeddingModelManifest?>();
            using var h = new Harness(null, lookup: lookup);

            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(h.Vm.CanApply).IsFalse();

            var offered = h.Vm.WhenAnyValue(x => x.CanApply).Where(can => can).FirstAsync().ToTask();
            lookup.SetResult(h.Offered);
            await offered.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Vm.Choice.UseBuiltin).IsTrue();
            await Assert.That(h.Vm.Choice.Choice.Unchosen).IsTrue();
            await Assert.That(h.Vm.Choice.Choice.Args).IsEmpty();
        });
    }

    [Test]
    public async Task The_recorded_choice_cannot_be_applied_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

            await Assert.That(h.Vm.Choice.UseDefault).IsTrue();
            await Assert.That(h.Vm.CanApply).IsFalse();

            h.Vm.Choice.UseKeyword = true;

            await Assert.That(h.Vm.CanApply).IsTrue();
        });
    }

    [Test]
    public async Task A_recorded_builtin_model_reads_as_search_by_meaning_and_only_a_change_applies() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)));

            await Assert.That(h.Vm.StateText).IsEqualTo("search by meaning");
            await Assert.That(h.Vm.CurrentText).IsEqualTo("Searches by meaning with the built-in model.");
            await Assert.That(h.Vm.CanApply).IsFalse();

            h.Vm.Choice.UseKeyword = true;

            await Assert.That(h.Vm.CanApply).IsTrue();
        });
    }

    [Test]
    [Arguments(0, true)]
    [Arguments(0, false)]
    [Arguments(1, true)]
    [Arguments(1, false)]
    public async Task An_upgrade_search_warning_allows_the_unchanged_builtin_choice_until_setup_succeeds(int exitCode, bool configured) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)));
            var canApply = false;
            using var subscription = h.Vm.WhenAnyValue(x => x.CanApply).Subscribe(value => canApply = value);
            const string skipped = """{"v":1,"event":"embeddings_skipped","code":"model_download_failed","message":"Model download failed"}""";
            h.Cli.LocalUpgradeBehavior = UpgradeScript.Prints(0,
                UpgradeScript.Planned("1.2.0", "1.3.0"), skipped, UpgradeScript.Upgraded("1.2.0", "1.3.0"));

            await h.Upgrade.StartAsync(CancellationToken.None);
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Vm.Choice.UseBuiltin).IsTrue();
            await Assert.That(canApply).IsTrue();
            await Assert.That(h.Vm.ApplyLabel).IsEqualTo("Try again");
            await Assert.That(h.Vm.StateText).IsEqualTo("keyword search");
            await Assert.That(h.Vm.CurrentText).Contains("keyword");

            await h.ApplyAsync(exitCode,
                configured ? """{"v":1,"event":"embeddings_configured","provider":"builtin"}""" : skipped,
                exitCode == 0 ? LocalSetupScript.Committed : LocalSetupScript.Failed("start_failed"));

            await Assert.That(h.Cli.SetupLocalEmbeddings).IsEquivalentTo([LocalEmbeddingChoice.Builtin], CollectionOrdering.Matching);
            var succeeded = exitCode == 0 && configured;
            await Assert.That(canApply).IsEqualTo(!succeeded);
            await Assert.That(h.Vm.ApplyLabel).IsEqualTo(succeeded ? "Save" : "Try again");
            await Assert.That(h.Vm.StateText).IsEqualTo(succeeded ? "search by meaning" : "keyword search");
        });
    }

    [Test]
    public async Task Turning_search_by_meaning_on_reruns_setup_with_the_model_and_reconnects() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Declined);
            h.Vm.Choice.UseOllama = true;

            h.Setting = LocalEmbeddingSetting.Ollama("mxbai-embed-large");
            await h.ApplyAsync(0,
                "{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"ollama\",\"model\":\"mxbai-embed-large\"}",
                Reused, LocalSetupScript.Committed);

            await Assert.That(h.Cli.SetupLocalEmbeddings).IsEquivalentTo([LocalEmbeddingChoice.Default], CollectionOrdering.Matching);
            await Assert.That(h.Vm.Embeddings.Summary).IsEqualTo("Search by meaning with mxbai-embed-large.");
            await Assert.That(h.Vm.StateText).IsEqualTo("search by meaning");
            await Assert.That(h.Vm.FailureText).IsNull();
            await Assert.That(h.Reconnects).IsEqualTo(1);
        });
    }

    [Test]
    public async Task A_run_another_kcap_holds_off_says_so() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            h.Vm.Choice.UseDefault = true;

            await h.ApplyAsync(1, LocalSetupScript.Failed("lifecycle_busy"));

            await Assert.That(h.Vm.FailureText).IsEqualTo(LocalSearchViewModel.BusyText);
            await Assert.That(h.Reconnects).IsEqualTo(0);
        });
    }

    [Test]
    public async Task Nothing_can_be_applied_while_an_upgrade_runs() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.LocalUpgradeBehavior = (_, _, _) => exit.Task;
            h.Vm.Choice.UseDefault = true;

            await h.Upgrade.StartAsync(CancellationToken.None);
            var during = h.Vm.CanApply;
            exit.SetResult(new StreamingResult(0, false, []));
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(during).IsFalse();
            await Assert.That(h.Vm.CanApply).IsTrue();
        });
    }

    [Test]
    public async Task An_upgrade_does_not_start_while_a_search_change_runs() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.SetupLocalBehavior = (_, _, _) => exit.Task;
            h.Vm.Choice.UseDefault = true;

            await h.Vm.ApplyCommand.Execute().ToTask();
            await h.Upgrade.StartAsync(CancellationToken.None);
            var upgrading = h.Upgrade.Running;
            exit.SetResult(new StreamingResult(0, false, []));
            await h.Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(upgrading).IsFalse();
            await Assert.That(h.Cli.LocalUpgradeCallCount).IsEqualTo(0);
        });
    }

    /// A setup run from a terminal changes the record and restarts the server; nothing else tells the app.
    [Test]
    public async Task A_reconnect_rereads_a_setting_changed_elsewhere() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

            h.Setting = LocalEmbeddingSetting.Declined;
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            var before = h.Vm.StateText;
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(before).IsEqualTo("search by meaning");
            await Assert.That(h.Vm.StateText).IsEqualTo("keyword search");
            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
        });
    }
}

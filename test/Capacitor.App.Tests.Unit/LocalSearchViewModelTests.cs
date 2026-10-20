using System.Reactive.Concurrency;
using System.Reactive.Threading.Tasks;
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
        public readonly LocalSearchController Search;
        public readonly LocalUpgradeController Upgrade;
        public readonly LocalSearchViewModel Vm;
        public int Reconnects;
        /// What `data/embedding.json` holds; the view model reads it again once a run settles.
        public LocalEmbeddingSetting? Setting;

        public Harness(LocalEmbeddingSetting? setting) {
            Setting = setting;
            Search  = new LocalSearchController(() => Cli, action => action());
            Upgrade = new LocalUpgradeController(() => Cli, action => action());
            Vm = new LocalSearchViewModel(() => Setting, Search, Upgrade, () => {
                Reconnects++;
                return Task.CompletedTask;
            }, CancellationToken.None, ImmediateScheduler.Instance);
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
            using var meaning = new Harness(new LocalEmbeddingSetting("mxbai-embed-large"));

            await Assert.That(keyword.Vm.StateText).IsEqualTo("keyword search");
            await Assert.That(meaning.Vm.StateText).IsEqualTo("search by meaning");
            await Assert.That(meaning.Vm.CurrentText).IsEqualTo("Searches by meaning with mxbai-embed-large.");
        });
    }

    [Test]
    public async Task The_recorded_choice_cannot_be_applied_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(new LocalEmbeddingSetting("mxbai-embed-large"));

            await Assert.That(h.Vm.Choice.UseDefault).IsTrue();
            await Assert.That(h.Vm.CanApply).IsFalse();

            h.Vm.Choice.UseKeyword = true;

            await Assert.That(h.Vm.CanApply).IsTrue();
            await Assert.That(h.Vm.ApplyLabel).IsEqualTo("Use keyword search");
        });
    }

    [Test]
    public async Task Turning_search_by_meaning_on_reruns_setup_with_the_model_and_reconnects() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Declined);
            h.Vm.Choice.UseDefault = true;
            await Assert.That(h.Vm.ApplyLabel).IsEqualTo("Turn on search by meaning");

            h.Setting = new LocalEmbeddingSetting("mxbai-embed-large");
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
}

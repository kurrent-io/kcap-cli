using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LocalStackViewModelTests {
    sealed class Harness : IDisposable {
        public readonly FakeKcapCli Cli = new();
        public readonly BehaviorSubject<ServerLaneStatus> Status = new(new(ServerLaneState.Connected));
        public readonly LocalUpgradeController Upgrade;
        public readonly LocalSearchController Search;
        public readonly LocalStackViewModel Vm;
        public int Reconnects;
        public int Reads;
        public int Starts;
        public Func<Task<ProcessResult>> StartResult = () => Task.FromResult(new ProcessResult(0, "Local server is running.\n", "", false));
        /// What the registry names; the view model reads it, never the run's target.
        public string? Registry;

        public Harness(string? installed = "1.2.0") {
            Registry = installed;
            Upgrade = new LocalUpgradeController(() => Cli, action => action(), held: () => Search.Running);
            Search  = new LocalSearchController(() => Cli, action => action(), held: () => Upgrade.Running);
            Vm = new LocalStackViewModel(() => {
                Reads++;
                return Registry;
            }, Status, Upgrade, _ => {
                Starts++;
                return StartResult();
            }, () => {
                Reconnects++;
                return Task.CompletedTask;
            }, CancellationToken.None, ImmediateScheduler.Instance, Search);
        }

        public async Task RunAsync(int exitCode, params string[] lines) {
            Cli.LocalUpgradeBehavior = UpgradeScript.Prints(exitCode, lines);
            await Upgrade.StartAsync(CancellationToken.None);
            await Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Vm.Dispose();
    }

    static IEnumerable<string?> ShownText(LocalStackViewModel vm) => [vm.Headline, vm.Detail, vm.Guidance, vm.StateText, vm.StartMessage];

    [Test]
    public async Task A_search_fallback_stays_visible_until_search_is_configured() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.RunAsync(0, UpgradeScript.Planned("1.2.0", "1.3.0"),
                """{"v":1,"event":"embeddings_skipped","code":"download_failed","message":"Model download failed"}""");

            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.IsNote).IsTrue();
            await Assert.That(h.Vm.Detail).Contains("keyword").And.Contains("Settings → Search");

            h.Cli.SetupLocalBehavior = LocalSetupScript.Prints(0,
                """{"v":1,"event":"embeddings_configured","provider":"builtin"}""", LocalSetupScript.Committed);
            await h.Search.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
            await h.Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Vm.BannerVisible).IsFalse();
        });
    }

    [Test]
    public async Task The_rail_shows_the_installed_version_and_the_servers_state() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await Assert.That(h.Vm.Version).IsEqualTo("1.2.0");
            await Assert.That(h.Vm.StateText).IsEqualTo("running");
            await Assert.That(h.Vm.BannerVisible).IsFalse();

            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(h.Vm.StateText).IsEqualTo("not running");
            await Assert.That(h.Vm.Attention).IsFalse();
        });
    }

    [Test]
    public async Task A_running_upgrade_shows_its_progress_in_the_banner_and_the_rail() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.LocalUpgradeBehavior = (onLine, _, _) => {
                onLine(new StreamedLine(ProcessStreamKind.Stdout, UpgradeScript.Planned("1.2.0", "1.3.0")));
                onLine(new StreamedLine(ProcessStreamKind.Stdout, "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":200}"));
                onLine(new StreamedLine(ProcessStreamKind.Stdout, "{\"v\":1,\"event\":\"download_progress\",\"received\":50,\"bytes\":200}"));
                return exit.Task;
            };

            await h.Upgrade.StartAsync(CancellationToken.None);

            await Assert.That(h.Vm.Upgrading).IsTrue();
            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.ActionsVisible).IsFalse();
            await Assert.That(h.Vm.Headline).IsEqualTo("Updating kcap local 1.2.0 → 1.3.0…");
            await Assert.That(h.Vm.Detail).IsEqualTo("Downloading 1.3.0 25%");
            await Assert.That(h.Vm.DownloadFraction).IsEqualTo(0.25);
            await Assert.That(h.Vm.StateText).IsNull();

            exit.SetResult(new StreamingResult(0, false, []));
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        });
    }

    [Test]
    public async Task An_upgrade_moves_the_rail_to_the_new_version_and_reconnects() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Registry = "1.3.0";

            await h.RunAsync(0, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Switched("1.3.0"),
                UpgradeScript.Upgraded("1.2.0", "1.3.0"));

            await Assert.That(h.Vm.Version).IsEqualTo("1.3.0");
            await Assert.That(h.Vm.BannerVisible).IsFalse();
            await Assert.That(h.Vm.Upgrading).IsFalse();
            await Assert.That(h.Reconnects).IsEqualTo(1);
        });
    }

    [Test]
    public async Task Nothing_to_do_shows_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await h.RunAsync(3, UpgradeScript.NothingToDo("current", "1.2.0", "1.2.0"));

            await Assert.That(h.Vm.BannerVisible).IsFalse();
            await Assert.That(h.Vm.Version).IsEqualTo("1.2.0");
            await Assert.That(h.Reconnects).IsEqualTo(0);
        });
    }

    [Test]
    public async Task A_failure_before_the_switch_offers_try_again_and_names_no_terminal_command() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await h.RunAsync(1, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Failed("download_failed",
                "The download was interrupted. kcap local 1.2.0 was left as it was. Run kcap local upgrade to try again.", false));

            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.Attention).IsTrue();
            await Assert.That(h.Vm.Headline).IsEqualTo("kcap local could not be updated to 1.3.0");
            await Assert.That(h.Vm.Detail).IsEqualTo("The download was interrupted. kcap local 1.2.0 was left as it was.");
            await Assert.That(h.Vm.TryAgainVisible).IsTrue();
            await Assert.That(h.Vm.StateText).IsEqualTo("update failed");
            await Assert.That(h.Vm.Version).IsEqualTo("1.2.0");
            foreach (var text in ShownText(h.Vm)) await Assert.That(text ?? "").DoesNotContain("kcap local upgrade");

            h.Cli.LocalUpgradeBehavior = UpgradeScript.Prints(0, UpgradeScript.Upgraded("1.2.0", "1.3.0"));
            h.Registry = "1.3.0";
            await h.Vm.TryAgainCommand.Execute().ToTask();
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Cli.LocalUpgradeCallCount).IsEqualTo(2);
            await Assert.That(h.Vm.BannerVisible).IsFalse();
            await Assert.That(h.Vm.Version).IsEqualTo("1.3.0");
        });
    }

    /// Another kcap holding the installation is not a failure of the stack: a quiet note, the server's
    /// own state in the rail, and Try again.
    [Test]
    public async Task A_busy_upgrade_is_a_note_that_keeps_the_servers_state() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await h.RunAsync(1, UpgradeScript.Planned("1.2.0", "1.3.0"),
                UpgradeScript.Failed("lifecycle_busy", "Another kcap local command holds the lifecycle lock.", false));

            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.IsNote).IsTrue();
            await Assert.That(h.Vm.Attention).IsFalse();
            await Assert.That(h.Vm.StateText).IsEqualTo("running");
            await Assert.That(h.Vm.Headline).IsEqualTo("kcap local 1.3.0 update is waiting");
            await Assert.That(h.Vm.Detail).IsEqualTo(LocalStackViewModel.BusyDetail);
            await Assert.That(h.Vm.TryAgainVisible).IsTrue();
            await Assert.That(h.Vm.ActionsVisible).IsTrue();
        });
    }

    /// The other kcap's change ends with the server coming back, which is when the upgrade can run.
    [Test]
    public async Task A_busy_upgrade_is_retried_when_the_server_comes_back() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.RunAsync(1, UpgradeScript.Failed("lifecycle_busy", "Another kcap local command holds the lifecycle lock.", false));
            h.Cli.LocalUpgradeBehavior = UpgradeScript.Prints(3, UpgradeScript.NothingToDo("current", "1.3.0", "1.3.0"));

            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Cli.LocalUpgradeCallCount).IsEqualTo(2);
            await Assert.That(h.Vm.BannerVisible).IsFalse();
        });
    }

    /// A search change can restart the server and holds the installation: the reconnect it causes must not start
    /// the waiting upgrade, nor offer Start, until the change is over.
    [Test]
    public async Task A_search_change_holds_a_waiting_upgrade_and_start_until_it_settles() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.RunAsync(1, UpgradeScript.Failed("lifecycle_busy", "Another kcap local command holds the lifecycle lock.", false));
            var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.SetupLocalBehavior = (_, _, _) => exit.Task;
            h.Cli.LocalUpgradeBehavior = UpgradeScript.Prints(3, UpgradeScript.NothingToDo("current", "1.2.0", "1.2.0"));

            await h.Search.StartAsync(LocalEmbeddingChoice.Default, CancellationToken.None);
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));
            var during = (h.Cli.LocalUpgradeCallCount, h.Vm.SuppressesServerStart, h.Vm.TryAgainVisible);

            exit.SetResult(new StreamingResult(0, false, []));
            await h.Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(during.LocalUpgradeCallCount).IsEqualTo(1);
            await Assert.That(during.SuppressesServerStart).IsTrue();
            await Assert.That(during.TryAgainVisible).IsFalse();
            await Assert.That(h.Cli.LocalUpgradeCallCount).IsEqualTo(2);
            await Assert.That(h.Vm.SuppressesServerStart).IsFalse();
        });
    }

    /// Exit 2 can come after Ready, with the server up and the upgrade journaled: connectivity proves
    /// nothing, so Start stays offered until it succeeds.
    [Test]
    public async Task A_failure_after_the_switch_offers_start_while_the_server_is_connected() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Registry = "1.3.0";

            await h.RunAsync(2, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Switched("1.3.0"),
                "{\"v\":1,\"event\":\"ready\",\"server_url\":\"http://localhost:5123\"}",
                UpgradeScript.Failed("start_failed", "kcap local 1.3.0 is selected, but the upgrade journal could not be cleared (busy). Nothing is rolled back; the next kcap local upgrade, start or setup --local finishes the upgrade.", true));
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.StartVisible).IsTrue();
            await Assert.That(h.Vm.ActionsVisible).IsTrue();
            await Assert.That(h.Vm.SuppressesServerStart).IsTrue();

            await h.Vm.StartCommand.Execute().ToTask();

            await Assert.That(h.Starts).IsEqualTo(1);
            await Assert.That(h.Vm.BannerVisible).IsFalse();
            await Assert.That(h.Vm.SuppressesServerStart).IsFalse();
            await Assert.That(h.Reconnects).IsEqualTo(1);
        });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task A_successful_start_keeps_only_an_unresolved_search_warning(bool configureBeforeStart, bool configureDuringStart) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness("1.3.0");
            using var settings = new LocalSearchViewModel(() => LocalEmbeddingSetting.Builtin(new string('a', 64)),
                _ => Task.FromResult<LocalEmbeddingModelManifest?>(null), h.Status, h.Search, h.Upgrade,
                () => Task.CompletedTask, CancellationToken.None, ImmediateScheduler.Instance);
            var exit = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.StartResult = () => exit.Task;
            h.Cli.SetupLocalBehavior = LocalSetupScript.Prints(0,
                """{"v":1,"event":"embeddings_configured","provider":"builtin"}""", LocalSetupScript.Committed);
            await h.RunAsync(2, UpgradeScript.Planned("1.2.0", "1.3.0"),
                """{"v":1,"event":"embeddings_skipped","code":"model_download_failed","message":"Model download failed"}""",
                UpgradeScript.Switched("1.3.0"), UpgradeScript.Failed("start_failed", "Server did not start.", true));

            if (configureBeforeStart) await ConfigureSearchAsync();
            var start = h.Vm.StartCommand.Execute().ToTask();
            if (configureDuringStart) await ConfigureSearchAsync();
            exit.SetResult(new ProcessResult(0, "Local server is running.", "", false));
            await start.WaitAsync(TimeSpan.FromSeconds(5));

            var warning = !configureBeforeStart && !configureDuringStart;
            await Assert.That(h.Vm.BannerVisible).IsEqualTo(warning);
            await Assert.That(h.Vm.IsNote).IsEqualTo(warning);
            await Assert.That(h.Vm.StartVisible).IsFalse();
            await Assert.That(h.Vm.Attention).IsFalse();
            await Assert.That(settings.CanApply).IsEqualTo(warning);
            if (warning) {
                await Assert.That(h.Vm.Headline).IsEqualTo("Search needs attention");
                await Assert.That(h.Vm.Detail).Contains("keyword").And.Contains("Settings → Search");
                await settings.ApplyCommand.Execute().ToTask();
                await h.Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(h.Vm.BannerVisible).IsFalse();
                await Assert.That(settings.CanApply).IsFalse();
            }

            async Task ConfigureSearchAsync() {
                await h.Search.StartAsync(LocalEmbeddingChoice.Builtin, CancellationToken.None);
                await h.Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        });
    }

    [Test]
    public async Task A_successful_start_does_not_replace_a_newer_upgrade_outcome() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness("1.3.0");
            var exit = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.StartResult = () => exit.Task;
            await h.RunAsync(2, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Switched("1.3.0"),
                UpgradeScript.Failed("start_failed", "Server did not start.", true));

            var start = h.Vm.StartCommand.Execute().ToTask();
            await h.RunAsync(4, UpgradeScript.Planned("1.3.0", "1.4.0"));
            exit.SetResult(new ProcessResult(0, "Local server is running.", "", false));
            await start.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.Headline).IsEqualTo("kcap local 1.4.0 is not available yet");
        });
    }

    /// Dismissing would take away the only Start while the upgrade is still journaled.
    [Test]
    public async Task A_failure_after_the_switch_cannot_be_dismissed() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await h.RunAsync(2, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Switched("1.3.0"),
                UpgradeScript.Failed("start_failed", "kcap local 1.3.0 did not start.", true));

            await Assert.That(h.Vm.DismissVisible).IsFalse();

            await h.Vm.DismissCommand.Execute().ToTask();

            await Assert.That(h.Vm.StartVisible).IsTrue();
            await Assert.That(h.Vm.Attention).IsTrue();
        });
    }

    [Test]
    public async Task A_failed_start_after_the_switch_keeps_the_failure_and_says_why_without_terminal_advice() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.StartResult = () => Task.FromResult(new ProcessResult(1, "", "kcap local 1.3.0 did not become ready. kcap local logs shows why.\n", false));

            await h.RunAsync(2, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Switched("1.3.0"),
                UpgradeScript.Failed("start_failed", "kcap local 1.3.0 did not start.", true));
            await h.Vm.StartCommand.Execute().ToTask();

            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.StartVisible).IsTrue();
            await Assert.That(h.Vm.StartMessage).IsEqualTo("kcap local 1.3.0 did not become ready.");
            await Assert.That(h.Reconnects).IsEqualTo(0);
        });
    }

    /// The new version stays selected and stopped: running the upgrade again changes nothing, so the
    /// way forward is Start, and the failure stays until it succeeds.
    [Test]
    public async Task A_failure_after_the_switch_points_at_start() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Registry = "1.3.0";

            await h.RunAsync(2, UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Switched("1.3.0"), UpgradeScript.Failed("start_failed",
                "kcap local 1.3.0 did not start: not ready. It stays selected and stopped. Nothing is rolled back; kcap local logs shows why, and kcap local start tries again.", true));
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));

            await Assert.That(h.Vm.Version).IsEqualTo("1.3.0");
            await Assert.That(h.Vm.Headline).IsEqualTo("kcap local 1.3.0 did not start after the update");
            await Assert.That(h.Vm.Guidance).IsEqualTo(LocalStackViewModel.StartGuidance);
            await Assert.That(h.Vm.TryAgainVisible).IsFalse();
            await Assert.That(h.Vm.StartVisible).IsTrue();
            foreach (var text in ShownText(h.Vm)) {
                await Assert.That(text ?? "").DoesNotContain("kcap local logs");
                await Assert.That(text ?? "").DoesNotContain("kcap local start");
            }

            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.BannerVisible).IsTrue();
        });
    }

    /// A failure the CLI marks as possibly switched, with no switch line, may have left the registry on
    /// either version: the rail names what the registry says, not the run's target.
    [Test]
    public async Task A_switch_that_was_not_confirmed_keeps_the_registrys_version() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await h.RunAsync(2, UpgradeScript.Planned("1.2.0", "1.3.0"),
                UpgradeScript.Failed("commit_failed", "The switch to kcap local 1.3.0 could not be confirmed (disk full).", true));

            await Assert.That(h.Vm.Version).IsEqualTo("1.2.0");
        });
    }

    /// A version change made outside the app restarts the server, and the reconnect rereads it.
    [Test]
    public async Task A_reconnect_rereads_the_registry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            var before = h.Reads;

            h.Registry = "1.4.0";
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.Version).IsEqualTo("1.4.0");
            await Assert.That(h.Reads).IsEqualTo(before + 1);
        });
    }

    [Test]
    public async Task A_bundle_not_yet_published_is_a_note_not_a_failure() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await h.RunAsync(4, UpgradeScript.Planned("1.2.0", "1.3.0"),
                UpgradeScript.Failed("bundle_not_published", "No bundle is published for 1.3.0 yet.", false));

            await Assert.That(h.Vm.BannerVisible).IsTrue();
            await Assert.That(h.Vm.Attention).IsFalse();
            await Assert.That(h.Vm.Headline).IsEqualTo("kcap local 1.3.0 is not available yet");
            await Assert.That(h.Vm.TryAgainVisible).IsTrue();
            await Assert.That(h.Vm.StateText).IsEqualTo("running");
        });
    }

    [Test]
    public async Task Dismiss_hides_a_settled_banner() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();

            await h.RunAsync(1, UpgradeScript.Failed("not_our_server", "The unit is not this installation's.", false));
            await Assert.That(h.Vm.TryAgainVisible).IsFalse();

            await h.Vm.DismissCommand.Execute().ToTask();

            await Assert.That(h.Vm.BannerVisible).IsFalse();
            await Assert.That(h.Vm.Attention).IsFalse();
        });
    }
}

using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class LocalUpgradeProgressTests {
    [Test]
    public async Task Model_bytes_replace_archive_progress_and_keyword_fallback_remains_visible() {
        var p = Run(
            """{"v":1,"event":"download_progress","received":200,"bytes":200}""",
            """{"v":1,"event":"embeddings_builtin_downloading","bytes":300}""",
            """{"v":1,"event":"embeddings_builtin_progress","received":99,"bytes":300}""");

        await Assert.That(p.DownloadFraction).IsEqualTo(0.33);
        await Assert.That(p.Phase.ToString()).IsEqualTo("DownloadingModel");

        p.Apply(LocalSetupLine.Parse("""{"v":1,"event":"embeddings_skipped","code":"download_failed","message":"Search by keyword. Run kcap setup --local to retry."}""")!);
        var outcome = LocalUpgradeOutcome.Settle(p, 0, null);
        var step = UpdateFinishViewModel.LocalStep(new(false, outcome, null, null))!;

        await Assert.That(outcome).IsTypeOf<LocalUpgradeOutcome.Upgraded>();
        await Assert.That(step.State).IsEqualTo(UpdateStepState.Note);
        await Assert.That(step.Detail).Contains("keyword");
    }

    static LocalUpgradeProgress Run(params string[] lines) {
        var progress = new LocalUpgradeProgress();
        foreach (var text in lines) {
            if (LocalSetupLine.Parse(text) is { } line) progress.Apply(line);
        }
        return progress;
    }

    [Test]
    public async Task Unknown_events_and_garbage_lines_change_nothing() {
        var p = Run("warning: x", "{\"v\":2,\"event\":\"future_upgrade_event\"}", "{\"v\":1,\"eve");

        await Assert.That(p.Phase).IsEqualTo(LocalUpgradePhase.Waiting);
        await Assert.That(p.From).IsNull();
        await Assert.That(p.To).IsNull();
        await Assert.That(p.FailedCode).IsNull();
    }

    [Test]
    public async Task A_full_upgrade_moves_through_every_phase() {
        var p = new LocalUpgradeProgress();
        var phases = new List<LocalUpgradePhase>();
        string[] lines = [
            UpgradeScript.Planned("1.2.0", "1.3.0"),
            "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":200}",
            "{\"v\":1,\"event\":\"download_progress\",\"received\":50,\"bytes\":200}",
            "{\"v\":1,\"event\":\"verified\",\"sha256\":\"x\"}",
            "{\"v\":1,\"event\":\"stopping\"}",
            "{\"v\":1,\"event\":\"stopped\"}",
            UpgradeScript.Switched("1.3.0"),
            "{\"v\":1,\"event\":\"starting\",\"unit\":\"io.kurrent.kcap.local\"}",
            "{\"v\":1,\"event\":\"ready\",\"server_url\":\"http://localhost:5123\"}",
            UpgradeScript.Upgraded("1.2.0", "1.3.0"),
        ];
        foreach (var line in lines) {
            p.Apply(LocalSetupLine.Parse(line)!);
            if (phases.Count == 0 || phases[^1] != p.Phase) phases.Add(p.Phase);
        }

        await Assert.That(phases).IsEquivalentTo(
            [LocalUpgradePhase.Waiting, LocalUpgradePhase.Downloading, LocalUpgradePhase.Extracting, LocalUpgradePhase.Stopping, LocalUpgradePhase.Starting],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(p.From).IsEqualTo("1.2.0");
        await Assert.That(p.To).IsEqualTo("1.3.0");
        await Assert.That(p.SwitchedTo).IsEqualTo("1.3.0");
        await Assert.That(p.ServerUrl).IsEqualTo(UpgradeScript.ServerUrl);
    }

    [Test]
    public async Task Download_progress_reports_a_fraction_and_none_without_a_size() {
        var sized = Run(
            "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":200}",
            "{\"v\":1,\"event\":\"download_progress\",\"received\":50,\"bytes\":200}");
        var unsized = Run(
            "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":0}",
            "{\"v\":1,\"event\":\"download_progress\",\"received\":10,\"bytes\":0}");

        await Assert.That(sized.DownloadFraction).IsEqualTo(0.25);
        await Assert.That(unsized.DownloadFraction).IsNull();
        await Assert.That(unsized.Phase).IsEqualTo(LocalUpgradePhase.Downloading);
    }

    [Test]
    public async Task A_failure_before_the_switch_restarts_the_old_version() {
        var p = Run(
            UpgradeScript.Planned("1.2.0", "1.3.0"),
            "{\"v\":1,\"event\":\"stopping\"}",
            "{\"v\":1,\"event\":\"restarting\",\"version\":\"1.2.0\"}",
            UpgradeScript.Failed("start_failed", "The local server could not be stopped (busy). Capacitor local 1.2.0 was started again.", false));

        await Assert.That(p.Phase).IsEqualTo(LocalUpgradePhase.Restarting);
        await Assert.That(p.FailedCode).IsEqualTo("start_failed");
        await Assert.That(p.FailedSwitched).IsFalse();
        await Assert.That(p.SwitchedTo).IsNull();
    }

    [Test]
    public async Task Nothing_to_do_records_its_reason_and_versions() {
        var p = Run(UpgradeScript.NothingToDo("stack_newer", "1.2.0", "1.3.0"));

        await Assert.That(p.SkipReason).IsEqualTo("stack_newer");
        await Assert.That(p.To).IsEqualTo("1.2.0");
        await Assert.That(p.From).IsEqualTo("1.3.0");
    }

    /// A resumed upgrade's switch is the earlier run's, not this one's: counting it would turn a failure
    /// before this run's own switch into one after it.
    [Test]
    public async Task A_switch_inside_a_recovery_is_not_this_runs_switch() {
        var p = Run(
            "{\"v\":1,\"event\":\"recovering\",\"operation\":\"upgrade\",\"phase\":\"switching\"}",
            UpgradeScript.Switched("1.2.5"),
            "{\"v\":1,\"event\":\"starting\",\"unit\":\"io.kurrent.kcap.local\"}",
            "{\"v\":1,\"event\":\"recovered\",\"operation\":\"upgrade\",\"phase\":\"switching\"}",
            UpgradeScript.Planned("1.2.5", "1.3.0"));

        await Assert.That(p.SwitchedTo).IsNull();
        await Assert.That(p.Phase).IsEqualTo(LocalUpgradePhase.Waiting);
    }

    [Test]
    public async Task A_recovery_shows_until_it_is_done() {
        var recovering = Run("{\"v\":1,\"event\":\"recovering\",\"operation\":\"upgrade\",\"phase\":\"starting\"}");
        var recovered = Run(
            "{\"v\":1,\"event\":\"recovering\",\"operation\":\"upgrade\",\"phase\":\"starting\"}",
            "{\"v\":1,\"event\":\"recovered\",\"operation\":\"upgrade\",\"phase\":\"starting\"}");

        await Assert.That(recovering.Phase).IsEqualTo(LocalUpgradePhase.Recovering);
        await Assert.That(recovered.Phase).IsEqualTo(LocalUpgradePhase.Waiting);
    }
}

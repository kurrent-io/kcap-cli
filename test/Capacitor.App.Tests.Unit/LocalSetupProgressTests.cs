using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.Tests.Unit;

public class LocalSetupProgressTests {
    static LocalSetupProgress Run(params string[] lines) {
        var progress = new LocalSetupProgress();
        foreach (var text in lines) {
            if (LocalSetupLine.Parse(text) is { } line) progress.Apply(line);
        }
        return progress;
    }

    [Test]
    public async Task Unknown_events_and_garbage_lines_change_nothing() {
        var p = Run("warning: x", "{\"v\":2,\"event\":\"embedding_pulling\"}", "{\"v\":1,\"eve", "[1]", "{\"v\":1}");

        await Assert.That(p.Download.State).IsEqualTo(RowState.Pending);
        await Assert.That(p.Verify.State).IsEqualTo(RowState.Pending);
        await Assert.That(p.Install.State).IsEqualTo(RowState.Pending);
        await Assert.That(p.FirstStart.State).IsEqualTo(RowState.Pending);
        await Assert.That(p.SawSuccess).IsFalse();
    }

    [Test]
    public async Task A_newer_version_line_is_still_read_for_its_event() {
        var p = Run("{\"v\":9,\"event\":\"committed\",\"server_url\":\"http://localhost:5000\"}");

        await Assert.That(p.SawSuccess).IsTrue();
        await Assert.That(p.ServerUrl).IsEqualTo("http://localhost:5000");
    }

    [Test]
    public async Task Download_without_a_size_is_indeterminate() {
        var p = Run(
            "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":0}",
            "{\"v\":1,\"event\":\"download_progress\",\"received\":10,\"bytes\":0}");

        await Assert.That(p.DownloadFraction).IsNull();
        await Assert.That(p.Download.State).IsEqualTo(RowState.Running);
    }

    [Test]
    public async Task Download_progress_reports_a_fraction() {
        var p = Run(
            "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":200}",
            "{\"v\":1,\"event\":\"download_progress\",\"received\":50,\"bytes\":200}");

        await Assert.That(p.DownloadFraction).IsEqualTo(0.25);
    }

    [Test]
    public async Task Rows_follow_the_lines() {
        var p = Run(
            "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":10}",
            "{\"v\":1,\"event\":\"verified\",\"sha256\":\"x\"}",
            "{\"v\":1,\"event\":\"unpacked\",\"path\":\"/p\"}",
            "{\"v\":1,\"event\":\"starting\",\"unit\":\"u\"}");

        await Assert.That(p.Download.State).IsEqualTo(RowState.Done);
        await Assert.That(p.Verify.State).IsEqualTo(RowState.Done);
        await Assert.That(p.Install.State).IsEqualTo(RowState.Done);
        await Assert.That(p.FirstStart.State).IsEqualTo(RowState.Running);
    }

    /// The CLI picks the port before it downloads, so only one row runs at a time in its real order.
    [Test]
    public async Task One_row_runs_at_a_time_in_the_order_the_cli_reports() {
        var p = Run("{\"v\":1,\"event\":\"port_chosen\",\"port\":5000}",
            "{\"v\":1,\"event\":\"downloading\",\"url\":\"u\",\"bytes\":10}");

        await Assert.That(p.Download.State).IsEqualTo(RowState.Running);
        await Assert.That(p.FirstStart.State).IsEqualTo(RowState.Pending);

        p.Apply(LocalSetupLine.Parse("{\"v\":1,\"event\":\"verified\",\"sha256\":\"x\"}")!);

        await Assert.That(p.Verify.State).IsEqualTo(RowState.Done);
        await Assert.That(p.Install.State).IsEqualTo(RowState.Running);
        await Assert.That(p.FirstStart.State).IsEqualTo(RowState.Pending);

        p.Apply(LocalSetupLine.Parse("{\"v\":1,\"event\":\"unpacked\",\"path\":\"/p\"}")!);

        await Assert.That(p.Install.State).IsEqualTo(RowState.Running);

        p.Apply(LocalSetupLine.Parse("{\"v\":1,\"event\":\"starting\",\"unit\":\"u\"}")!);

        await Assert.That(p.Install.State).IsEqualTo(RowState.Done);
        await Assert.That(p.FirstStart.State).IsEqualTo(RowState.Running);
    }

    [Test]
    public async Task Committed_after_reused_is_one_success() {
        var p = Run(
            "{\"v\":1,\"event\":\"reused\",\"server_url\":\"http://localhost:5000\",\"installed_version\":\"1.0.0\"}",
            "{\"v\":1,\"event\":\"committed\",\"server_url\":\"http://localhost:5000\",\"installation_id\":\"i\",\"port\":5000,\"version\":\"1.0.0\"}");

        await Assert.That(p.SawSuccess).IsTrue();
        await Assert.That(p.Reused).IsTrue();
        await Assert.That(p.Plan).IsEqualTo("reused");
        await Assert.That(p.FirstStart.State).IsEqualTo(RowState.Done);
    }

    [Test]
    public async Task A_recovery_that_commits_is_planned_as_recovered() {
        var p = Run(
            "{\"v\":1,\"event\":\"recovering\",\"operation\":\"install\",\"phase\":\"p\"}",
            "{\"v\":1,\"event\":\"recovered\",\"operation\":\"install\",\"phase\":\"p\"}",
            "{\"v\":1,\"event\":\"committed\",\"server_url\":\"http://localhost:5000\"}");

        await Assert.That(p.Recovering).IsFalse();
        await Assert.That(p.Plan).IsEqualTo("recovered");
    }

    [Test]
    public async Task Rolling_back_is_shown_until_rolled_back() {
        var p = Run("{\"v\":1,\"event\":\"rolling_back\",\"reason\":\"r\"}");
        await Assert.That(p.RollingBack).IsTrue();

        p.Apply(LocalSetupLine.Parse("{\"v\":1,\"event\":\"rolled_back\"}")!);
        await Assert.That(p.RollingBack).IsFalse();
    }
}

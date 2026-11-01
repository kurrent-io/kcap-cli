using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.Tests.Unit;

public class LocalSetupProgressTests {
    const string OllamaNotFound =
        "{\"v\":1,\"event\":\"embeddings_skipped\",\"code\":\"ollama_not_found\",\"message\":\"Ollama is not responding on http://localhost:11434; search works by keyword. Start Ollama, then run kcap setup --local again to turn on search by meaning.\"}";

    static LocalSetupProgress Run(params string[] lines) => Asked(null, lines);

    static LocalSetupProgress Asked(LocalEmbeddingChoice? asked, params string[] lines) {
        var progress = new LocalSetupProgress(asked);
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

    [Test]
    public async Task A_model_pull_runs_the_search_row_and_finishes_installing() {
        var p = Run(
            "{\"v\":1,\"event\":\"unpacked\"}",
            "{\"v\":1,\"event\":\"embeddings_ollama_found\",\"version\":\"0.12.3\",\"url\":\"http://localhost:11434\"}",
            "{\"v\":1,\"event\":\"embeddings_model_pulling\",\"model\":\"mxbai-embed-large\"}",
            "{\"v\":1,\"event\":\"embeddings_model_progress\",\"model\":\"mxbai-embed-large\",\"received\":50,\"bytes\":200}");

        await Assert.That(p.Install.State).IsEqualTo(RowState.Done);
        await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Running);
        await Assert.That(p.Embeddings.Pulling).IsTrue();
        await Assert.That(p.Embeddings.PullFraction).IsEqualTo(0.25);
        await Assert.That(p.Embeddings.PullText).IsEqualTo("Downloading the Ollama model mxbai-embed-large… 25%");
        await Assert.That(p.Embeddings.Summary).IsNull();
    }

    [Test]
    public async Task A_builtin_download_runs_the_search_row_and_ends_in_the_builtin_model() {
        var p = Run(
            "{\"v\":1,\"event\":\"embeddings_builtin_downloading\",\"bytes\":336592896}");

        await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Running);
        await Assert.That(p.Embeddings.PullText).IsEqualTo("Downloading the built-in search model (321 MB)…");

        p.Apply(LocalSetupLine.Parse("{\"v\":1,\"event\":\"embeddings_builtin_progress\",\"received\":84148224,\"bytes\":336592896}")!);

        await Assert.That(p.Embeddings.PullText).IsEqualTo("Downloading the built-in search model (321 MB)… 25%");

        p.Apply(LocalSetupLine.Parse("{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"builtin\"}")!);

        await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Done);
        await Assert.That(p.Embeddings.Configured).IsTrue();
        await Assert.That(p.Embeddings.Summary).IsEqualTo("Search by meaning with the built-in model.");
        await Assert.That(p.Embeddings.ReembedNote).IsEqualTo("The search index for saved information is being built with the built-in model in the background.");
    }

    [Test]
    public async Task A_switch_to_the_builtin_model_names_the_Ollama_model_it_replaced() {
        var p = Run("{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"builtin\",\"replaced_model\":\"mxbai-embed-large\"}");

        await Assert.That(p.Embeddings.ReplacedModel).IsEqualTo("mxbai-embed-large");
        await Assert.That(p.Embeddings.ReembedNote).IsEqualTo("The search index is being rebuilt with the built-in model in the background after switching from mxbai-embed-large. Your saved information is kept.");
    }

    [Test]
    public async Task A_configured_model_ends_in_search_by_meaning_and_names_a_replaced_one() {
        var p = Run(
            "{\"v\":1,\"event\":\"embeddings_model_ready\",\"model\":\"nomic-embed-text\",\"dimensions\":768}",
            "{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"ollama\",\"model\":\"nomic-embed-text\",\"replaced_model\":\"mxbai-embed-large\"}");

        await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Done);
        await Assert.That(p.Embeddings.Pulling).IsFalse();
        await Assert.That(p.Embeddings.Summary).IsEqualTo("Search by meaning with nomic-embed-text.");
        await Assert.That(p.Embeddings.ReembedNote!).Contains("rebuilt with nomic-embed-text in the background");
    }

    /// The CLI's skip message is written for a terminal; the row keeps its cause and drops its advice.
    [Test]
    public async Task A_skip_ends_in_keyword_search_with_its_reason_and_fails_nothing() {
        var p = Run(OllamaNotFound, "{\"v\":1,\"event\":\"starting\",\"unit\":\"u\"}");

        await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Skipped);
        await Assert.That(p.Embeddings.Skipped).IsTrue();
        await Assert.That(p.Embeddings.Summary).IsEqualTo("Ollama is not responding on http://localhost:11434; search works by keyword.");
        await Assert.That(p.FailedCode).IsNull();
        await Assert.That(p.FirstStart.State).IsEqualTo(RowState.Running);
    }

    [Test]
    public async Task A_decline_reads_as_chosen_rather_than_the_cli_flag_advice() {
        var p = Run(
            "{\"v\":1,\"event\":\"embeddings_skipped\",\"code\":\"declined\",\"message\":\"Search by meaning stays off, as an earlier setup chose; --embedding-model <model> turns it on.\"}");

        await Assert.That(p.Embeddings.Summary).IsEqualTo("Search by keyword, as chosen.");
    }

    /// Keyword search left as the recommendation passes no flag, so setup still probes Ollama and reports it missing.
    [Test]
    public async Task A_skip_after_keyword_search_was_asked_for_reads_as_that_choice() {
        foreach (var asked in new[] { LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.KeywordOnly), LocalEmbeddingChoice.KeywordOnly }) {
            var p = Asked(asked, OllamaNotFound);

            await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Skipped);
            await Assert.That(p.Embeddings.Summary).IsEqualTo("Search by keyword, as chosen.");
        }
    }

    [Test]
    public async Task A_skip_after_search_by_meaning_was_asked_for_keeps_its_reason() {
        foreach (var asked in new[] { LocalEmbeddingChoice.Default, LocalEmbeddingChoice.Builtin, LocalEmbeddingChoice.Recommended(LocalEmbeddingChoice.Builtin) }) {
            var p = Asked(asked, OllamaNotFound);

            await Assert.That(p.Embeddings.Summary).IsEqualTo("Ollama is not responding on http://localhost:11434; search works by keyword.");
        }
    }

    /// The server already serves the model when setup fails to clear its pending mark.
    [Test]
    public async Task A_failed_record_after_configuring_leaves_search_by_meaning() {
        var p = Run(
            "{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"ollama\",\"model\":\"mxbai-embed-large\"}",
            "{\"v\":1,\"event\":\"embeddings_skipped\",\"code\":\"write_failed\",\"message\":\"/x/embedding.json could not be written (disk full).\"}");

        await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Done);
        await Assert.That(p.Embeddings.Skipped).IsFalse();
        await Assert.That(p.Embeddings.Summary).IsEqualTo("Search by meaning with mxbai-embed-large.");
    }

    [Test]
    public async Task A_configured_model_with_nothing_replaced_still_says_stored_sessions_are_embedded() {
        var p = Run("{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"ollama\",\"model\":\"mxbai-embed-large\"}");

        await Assert.That(p.Embeddings.ReembedNote).IsEqualTo("The search index for saved information is being built with mxbai-embed-large in the background.");
    }

    [Test]
    public async Task A_run_that_halts_mid_pull_leaves_the_search_row_pending() {
        var p = Run("{\"v\":1,\"event\":\"embeddings_model_pulling\",\"model\":\"m\"}");

        p.Halt();

        await Assert.That(p.Embeddings.Row.State).IsEqualTo(RowState.Pending);
        await Assert.That(p.Embeddings.Pulling).IsFalse();
    }
}

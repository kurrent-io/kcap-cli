using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Gemini;

namespace Capacitor.Cli.Tests.Unit.Harness.Gemini;

/// <summary>
/// discovery walks <c>~/.gemini/tmp/&lt;project&gt;/chats/session-*.jsonl</c>
/// and reads the FULL dashed session id from the file's header record (the
/// filename only carries the 8-char shortId). <see cref="GeminiImportSource.IsImportRelevantLine"/>
/// must mirror the server normalizer's skip rules so the import watermark
/// compares against the right line.
/// </summary>
public class GeminiImportSourceTests {
    static void WriteSession(string tmpDir, string project, string fileName, string sessionId, params string[] extraLines) {
        var chats = Path.Combine(tmpDir, project, "chats");
        Directory.CreateDirectory(chats);

        var header = $$"""{"sessionId":"{{sessionId}}","projectHash":"abc","startTime":"2026-06-17T14:10:03.447Z","kind":"main"}""";
        File.WriteAllLines(Path.Combine(chats, fileName), new[] { header }.Concat(extraLines));
    }

    [Test]
    public async Task discover_reads_full_session_id_from_header() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, "proj", "session-2026-06-17T14-10-11111111.jsonl",
            "11111111-1111-1111-1111-111111111111",
            """{"id":"u1","timestamp":"t","type":"user","content":[{"text":"hi"}]}""");

        var source = new GeminiImportSource(tmp.Path);
        await Assert.That(source.IsAvailable).IsTrue();

        var found = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].Vendor).IsEqualTo(HarnessId.Gemini);
        // Dashless FULL id from the header — not the 8-char filename shortId.
        await Assert.That(found[0].SessionId).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    public async Task discover_filter_by_session_matches_dashless_id() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, "proj", "session-a-22222222.jsonl", "22222222-2222-2222-2222-222222222222");
        WriteSession(tmp.Path, "proj", "session-b-33333333.jsonl", "33333333-3333-3333-3333-333333333333");

        var source = new GeminiImportSource(tmp.Path);
        var found  = await source.DiscoverAsync(
            new DiscoveryFilters(null, "22222222-2222-2222-2222-222222222222", null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo("22222222222222222222222222222222");
    }

    // The bootstrap is the only on-disk record of where a Gemini session ran, so discovery
    // reads it: without a cwd, capture scope cannot place the session and an allow list drops it.
    const string Bootstrap =
        "<session_context>\\nThis is the Gemini CLI.\\n"
      + "- **Workspace Directories:**\\n  - /work/demo\\n- **Directory Structure:**\\n</session_context>";

    static string BootstrapSeed() =>
        $$$"""{"$set":{"messages":[{"id":"d0","timestamp":"t","type":"user","content":[{"text":"{{{Bootstrap}}}"}]}],"lastUpdated":"t"}}""";

    [Test]
    public async Task discover_reads_the_workspace_from_the_session_context_bootstrap() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, "proj", "session-2026-06-17T14-10-44444444.jsonl",
            "44444444-4444-4444-4444-444444444444", BootstrapSeed());

        var found = await new GeminiImportSource(tmp.Path)
            .DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].Cwd).IsEqualTo("/work/demo");
    }

    [Test]
    public async Task discover_leaves_cwd_null_when_no_bootstrap_names_one() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, "proj", "session-2026-06-17T14-10-55555555.jsonl",
            "55555555-5555-5555-5555-555555555555",
            """{"id":"u1","timestamp":"t","type":"user","content":[{"text":"hi"}]}""");

        var found = await new GeminiImportSource(tmp.Path)
            .DiscoverAsync(new DiscoveryFilters(null, null, null, 1), CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].Cwd).IsNull();
    }

    [Test]
    public async Task discover_filter_by_cwd_matches_the_recorded_workspace() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, "proj", "session-a-66666666.jsonl",
            "66666666-6666-6666-6666-666666666666", BootstrapSeed());
        WriteSession(tmp.Path, "proj", "session-b-77777777.jsonl",
            "77777777-7777-7777-7777-777777777777",
            """{"id":"u1","timestamp":"t","type":"user","content":[{"text":"hi"}]}""");

        var source = new GeminiImportSource(tmp.Path);

        var matched = await source.DiscoverAsync(
            new DiscoveryFilters("/work/demo", null, null, 1), CancellationToken.None);
        await Assert.That(matched.Count).IsEqualTo(1);
        await Assert.That(matched[0].SessionId).IsEqualTo("66666666666666666666666666666666");

        // A session with no recorded workspace cannot satisfy the filter either.
        var elsewhere = await source.DiscoverAsync(
            new DiscoveryFilters("/work/other", null, null, 1), CancellationToken.None);
        await Assert.That(elsewhere.Count).IsEqualTo(0);
    }

    [Test]
    public async Task unavailable_when_tmp_dir_missing() {
        using var tmp = new TempDir();
        var source = new GeminiImportSource(tmp.PathTo("does-not-exist"));
        await Assert.That(source.IsAvailable).IsFalse();
    }

    // Mirror of the GeminiTranscriptNormalizer skip rules.
    [Test]
    [Arguments("""{"sessionId":"s","projectHash":"h","kind":"main"}""", false)]                                                              // header
    [Arguments("""{"$set":{"lastUpdated":"t"}}""", false)]                                                                                    // mutation op
    [Arguments("""{"id":"u","type":"user","content":[{"text":"<session_context>x</session_context>"}]}""", false)]                            // bootstrap
    [Arguments("""{"id":"u","type":"user","content":[{"functionResponse":{"id":"f","name":"read","response":{"output":"x"}}}]}""", false)]    // tool-result echo
    [Arguments("""{"id":"u","type":"user","content":[{"text":"real prompt"}]}""", true)]                                                      // real user prompt
    [Arguments("""{"id":"g","type":"gemini","content":"answer"}""", true)]                                                                    // gemini turn
    public async Task import_relevant_line_mirrors_normalizer_skips(string line, bool expected) {
        await Assert.That(GeminiImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
    }
}

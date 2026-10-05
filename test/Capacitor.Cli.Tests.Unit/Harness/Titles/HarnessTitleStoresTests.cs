using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Harness.Titles;
using Capacitor.Cli.Tests.Unit.Harness.Antigravity;
using Capacitor.Cli.Tests.Unit.Harness.Cursor;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Harness.Titles;

/// <summary>Each vendor's store is laid out as that vendor writes it under a home, and located only from the
/// transcript path and session id the watcher itself is given — so a reader pointed at the wrong file fails here.</summary>
public class HarnessTitleStoresTests {
    [TempHome] public required TempHome Home { get; init; }

    const string Id = "0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    static string Write(string path, string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task Codex_reads_the_session_index() {
        Home.CreateFile([".codex", "session_index.jsonl"], $$"""{"id":"{{Id}}","thread_name":"Codex name","updated_at":"2026-09-29T10:00:00Z"}""" + "\n");
        var transcript = Home.CreateFile([".codex", "sessions", "2026", "09", "29", $"rollout-2026-09-29T10-00-00-{Id}.jsonl"]);

        var store = HarnessTitleStores.For("codex", null, Id.Replace("-", ""), transcript, Harnesses);

        await Assert.That(store!.Read()!.Title).IsEqualTo("Codex name");
    }

    [Test]
    public async Task Copilot_reads_the_sibling_workspace_yaml() {
        var paths = Harnesses.Of<CopilotHarness>().Paths;
        Write(paths.WorkspaceYaml(paths.SessionStateDir, Id), "name: Copilot name\nuser_named: true\n");
        var transcript = Write(paths.EventsJsonl(paths.SessionStateDir, Id), "");

        var store = HarnessTitleStores.For("copilot", null, Id, transcript, Harnesses);

        await Assert.That(store!.Read()!.Title).IsEqualTo("Copilot name");
    }

    [Test]
    public async Task Kiro_reads_the_session_json_sidecar() {
        var paths = Harnesses.Of<KiroHarness>().Paths;
        Write(paths.SessionJson(Id), """{"title":"Kiro name"}""");
        var transcript = Write(paths.SessionJsonl(Id), "");

        var store = HarnessTitleStores.For("kiro", null, Id, transcript, Harnesses);

        await Assert.That(store!.Read()!.Title).IsEqualTo("Kiro name");
    }

    string CursorTranscript() =>
        Home.CreateFile([".cursor", "projects", "Users-me-work", "agent-transcripts", Id, $"{Id}.jsonl"]);

    // Windows keeps the IDE's store under the real Roaming AppData, which no test home can stand in for.
    [Test, ExcludeOn(OS.Windows)]
    public async Task Cursor_reads_the_chat_meta_json_first() {
        Home.CreateFile([".cursor", "chats", "0164d96655f5a60b647a4bd178a34425", Id, "meta.json"], """{"title":"From meta"}""");
        CursorComposerTitleTests.BuildStateDb(Write(Harnesses.Of<CursorHarness>().Paths.GlobalStateDb!, ""), Id, "From composer");

        var store = HarnessTitleStores.For("cursor", null, Id, CursorTranscript(), Harnesses);

        await Assert.That(store!.Read()!.Title).IsEqualTo("From meta");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Cursor_falls_back_to_the_ide_composer_record() {
        CursorComposerTitleTests.BuildStateDb(Write(Harnesses.Of<CursorHarness>().Paths.GlobalStateDb!, ""), Id, "From composer");

        var store = HarnessTitleStores.For("cursor", null, Id, CursorTranscript(), Harnesses);

        await Assert.That(store!.Read()!.Title).IsEqualTo("From composer");
    }

    [Test]
    public async Task Antigravity_reads_the_roots_summary_db() {
        const string conversationId = AntigravitySummaryTitleTests.ConversationId;
        var paths = Harnesses.Of<AntigravityHarness>().Paths;
        AntigravitySummaryTitleTests.BuildSummaryDb(Write(Path.Combine(paths.CliConfigRoot, "conversation_summaries.db"), ""), "Antigravity name");
        var transcript = Write(paths.TranscriptFullPathUnder(paths.CliConfigRoot, conversationId), "");

        var store = HarnessTitleStores.For("antigravity", null, conversationId.Replace("-", ""), transcript, Harnesses);

        await Assert.That(store!.Read()!.Title).IsEqualTo("Antigravity name");
    }

    [Test]
    [Arguments("claude")]
    [Arguments("pi")]
    [Arguments("gemini")]
    [Arguments("opencode")]
    public async Task Vendors_with_inline_titles_have_no_store(string vendor) {
        await Assert.That(HarnessTitleStores.For(vendor, null, Id, Home.PathTo("t.jsonl"), Harnesses)).IsNull();
    }

    [Test]
    public async Task A_subagent_watcher_has_no_store() {
        await Assert.That(HarnessTitleStores.For("codex", "agent-1", Id, Home.PathTo("t.jsonl"), Harnesses)).IsNull();
    }
}

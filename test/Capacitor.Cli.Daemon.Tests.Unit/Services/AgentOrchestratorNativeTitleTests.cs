using System.Globalization;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorNativeTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    TitleAgentView Agent(string vendor, params string[] lines) =>
        new("a1", vendor, null, "sid-1", Tmp.CreateFile("t.jsonl", string.Join('\n', lines) + "\n"), DateTime.UtcNow);

    [Test]
    public async Task A_rename_carries_its_change_time() {
        var post = AgentOrchestrator.NativeTitleFor(Agent("claude",
            """{"type":"user","timestamp":"2026-09-29T10:00:00Z"}""",
            """{"type":"custom-title","customTitle":"Mine","sessionId":"s"}"""));

        await Assert.That(post).IsEqualTo(new HarnessTitlePost("Mine", HarnessTitleKind.Rename,
            DateTimeOffset.Parse("2026-09-29T10:00:00Z", CultureInfo.InvariantCulture)));
    }

    [Test]
    public async Task An_ai_title_is_auto_and_untimed_even_after_stamped_lines() {
        var post = AgentOrchestrator.NativeTitleFor(Agent("claude",
            """{"type":"user","timestamp":"2026-09-29T10:00:00Z"}""",
            """{"type":"ai-title","aiTitle":"Auto","sessionId":"s"}"""));

        await Assert.That(post).IsEqualTo(new HarnessTitlePost("Auto", HarnessTitleKind.Auto, null));
    }

    [Test]
    public async Task A_non_claude_vendor_has_no_native_title() {
        var post = AgentOrchestrator.NativeTitleFor(Agent("codex", """{"type":"ai-title","aiTitle":"Auto","sessionId":"s"}"""));

        await Assert.That(post).IsNull();
    }
}

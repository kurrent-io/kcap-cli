using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit.Commands;

public class TranscriptTitleLinesTests {
    [Test]
    [Arguments("claude", """{"type":"ai-title","aiTitle":"x","sessionId":"s"}""", true)]
    [Arguments("claude", """{"type":"custom-title","customTitle":"x","sessionId":"s"}""", true)]
    [Arguments("claude", """{"type":"user","message":{"content":"ai-title"}}""", false)]
    [Arguments("pi", """{"type":"session_info","name":"x"}""", true)]
    [Arguments("pi", """{"type":"session_info","name":""}""", false)]
    [Arguments("gemini", """{"$set":{"summary":"x"}}""", true)]
    [Arguments("opencode", """{"type":"session_title","title":"x","time":1}""", true)]
    [Arguments("opencode", """{"type":"session_title","title":"New session - 2026-09-29T10:00:00Z","time":1}""", false)]
    [Arguments("codex", """{"type":"ai-title","aiTitle":"x"}""", false)]
    public async Task Detects_the_vendors_own_title_lines(string vendor, string line, bool expected) =>
        await Assert.That(TranscriptTitleLines.CarriesHarnessTitle(vendor, line)).IsEqualTo(expected);
}

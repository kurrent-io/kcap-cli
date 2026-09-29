using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Core.Tests.Unit.Commands;

public class TranscriptTitleLinesTests {
    [Test]
    [Arguments("claude", """{"type":"ai-title","aiTitle":"x","sessionId":"s"}""", TranscriptTitleLineKind.RecordedByEveryServer)]
    [Arguments("claude", """{"type":"custom-title","customTitle":"x","sessionId":"s"}""", TranscriptTitleLineKind.RecordedWithHarnessTitles)]
    [Arguments("claude", """{"type":"user","message":{"content":"ai-title"}}""", TranscriptTitleLineKind.None)]
    [Arguments("claude", """{"type":"ai-title","aiTitle":"x","sessionId":"other"}""", TranscriptTitleLineKind.None)]
    [Arguments("claude", """{"type":"summary","summary":"x","leafUuid":"u"}""", TranscriptTitleLineKind.RecordedWithHarnessTitles)]
    [Arguments("claude", """{"type":"summary","summary":"x","sessionId":"other"}""", TranscriptTitleLineKind.None)]
    [Arguments("pi", """{"type":"session_info","name":"x"}""", TranscriptTitleLineKind.RecordedWithHarnessTitles)]
    [Arguments("pi", """{"type":"session_info","name":""}""", TranscriptTitleLineKind.None)]
    [Arguments("gemini", """{"$set":{"summary":"x"}}""", TranscriptTitleLineKind.RecordedWithHarnessTitles)]
    [Arguments("opencode", """{"type":"session_title","title":"x","time":1}""", TranscriptTitleLineKind.RecordedWithHarnessTitles)]
    [Arguments("opencode", """{"type":"session_title","title":"New session - 2026-09-29T10:00:00.000Z","time":1}""", TranscriptTitleLineKind.None)]
    [Arguments("codex", """{"type":"ai-title","aiTitle":"x"}""", TranscriptTitleLineKind.None)]
    public async Task Classifies_the_vendors_own_title_lines(string vendor, string line, TranscriptTitleLineKind expected) =>
        await Assert.That(TranscriptTitleLines.Classify(vendor, "s", line)).IsEqualTo(expected);
}

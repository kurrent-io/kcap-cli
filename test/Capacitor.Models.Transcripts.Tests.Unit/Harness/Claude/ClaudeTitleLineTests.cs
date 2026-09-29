using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class ClaudeTitleLineTests {
    /// <summary>The legacy <c>summary</c> shape is not a title the server records, so it must not count as one here even
    /// though <see cref="ClaudeNativeTitle"/> displays it.</summary>
    [Test]
    [Arguments("""{"type":"ai-title","aiTitle":"x","sessionId":"s"}""", true, false)]
    [Arguments("""{"type":"custom-title","customTitle":"x","sessionId":"s"}""", true, true)]
    [Arguments("""{"type":"custom-title","customTitle":"  ","sessionId":"s"}""", false, true)]
    [Arguments("""{"type":"summary","summary":"x","leafUuid":"u"}""", false, false)]
    [Arguments("""{"type":"user","message":{"content":"ai-title"}}""", false, false)]
    [Arguments("""{"type":"ai-title","aiTitle":""", false, false)]
    public async Task Recognizes_only_the_title_records_the_server_records(string line, bool expected, bool expectedRename) {
        await Assert.That(ClaudeTitleLine.CarriesTitle(line, out var isRename)).IsEqualTo(expected);
        await Assert.That(isRename).IsEqualTo(expectedRename);
    }
}

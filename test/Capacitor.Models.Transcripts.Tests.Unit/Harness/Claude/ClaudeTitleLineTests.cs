using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class ClaudeTitleLineTests {
    /// <summary>The legacy <c>summary</c> shape is not a title the server records, so it must not count as one here even
    /// though <see cref="ClaudeNativeTitle"/> displays it.</summary>
    [Test]
    [Arguments("""{"type":"ai-title","aiTitle":"x","sessionId":"s"}""", true)]
    [Arguments("""{"type":"custom-title","customTitle":"x","sessionId":"s"}""", true)]
    [Arguments("""{"type":"custom-title","customTitle":"  ","sessionId":"s"}""", false)]
    [Arguments("""{"type":"summary","summary":"x","leafUuid":"u"}""", false)]
    [Arguments("""{"type":"user","message":{"content":"ai-title"}}""", false)]
    [Arguments("""{"type":"ai-title","aiTitle":""", false)]
    public async Task Recognizes_only_the_title_records_the_server_records(string line, bool expected) =>
        await Assert.That(ClaudeTitleLine.CarriesTitle(line)).IsEqualTo(expected);
}

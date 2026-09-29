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
        await Assert.That(ClaudeTitleLine.CarriesTitle(line, "s", out var isRename)).IsEqualTo(expected);
        await Assert.That(isRename).IsEqualTo(expectedRename);
    }

    /// <summary>A resumed session carries the history of the one it resumed, title lines included; the server records a
    /// title line only for the session its <c>sessionId</c> names, in either GUID form.</summary>
    [Test]
    [Arguments("""{"type":"ai-title","aiTitle":"x","sessionId":"5cdc4fcc-ff9e-4fdc-8d93-cf47ff5a140a"}""", true)]
    [Arguments("""{"type":"ai-title","aiTitle":"x","sessionId":"5CDC4FCCFF9E4FDC8D93CF47FF5A140A"}""", true)]
    [Arguments("""{"type":"ai-title","aiTitle":"x","sessionId":"11111111-2222-3333-4444-555555555555"}""", false)]
    [Arguments("""{"type":"custom-title","customTitle":"x","sessionId":"11111111-2222-3333-4444-555555555555"}""", false)]
    [Arguments("""{"type":"ai-title","aiTitle":"x"}""", false)]
    public async Task Counts_only_a_title_line_of_the_watched_session(string line, bool expected) =>
        await Assert.That(ClaudeTitleLine.CarriesTitle(line, "5cdc4fccff9e4fdc8d93cf47ff5a140a", out _)).IsEqualTo(expected);
}

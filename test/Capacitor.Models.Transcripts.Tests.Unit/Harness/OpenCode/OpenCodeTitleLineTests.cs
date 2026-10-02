using Capacitor.Models.Transcripts.Harness.OpenCode;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.OpenCode;

public class OpenCodeTitleLineTests {
    [Test]
    [Arguments("""{"type":"session_title","title":"x","time":1}""", true)]
    [Arguments("""{"type":"session_title","title":"x"}""", true)]
    [Arguments("""{"type":"session_title","title":"New session - 2026-09-29T10:00:00.000Z","time":1}""", false)]
    [Arguments("""{"type":"session_title","title":""}""", false)]
    public async Task Recognizes_a_real_session_title_record(string line, bool expected) =>
        await Assert.That(OpenCodeTitleLine.CarriesTitle(line)).IsEqualTo(expected);

    [Test]
    public async Task Only_the_seeded_default_is_a_placeholder() {
        await Assert.That(OpenCodeTitleLine.IsPlaceholder("New session - 2026-09-29T10:00:00.000Z")).IsTrue();
        await Assert.That(OpenCodeTitleLine.IsPlaceholder("New session planning")).IsFalse();
    }
}

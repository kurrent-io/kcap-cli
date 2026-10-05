using Capacitor.Models.Transcripts.Harness.Gemini;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Gemini;

public class GeminiTitleLineTests {
    [Test]
    [Arguments("""{"$set":{"summary":"x"}}""", true)]
    [Arguments("""{"$set":{"summary":" "}}""", false)]
    [Arguments("""{"$set":{"other":"summary"}}""", false)]
    [Arguments("""{"summary":"x"}""", false)]
    public async Task Recognizes_a_set_patch_with_a_summary(string line, bool expected) =>
        await Assert.That(GeminiTitleLine.CarriesTitle(line)).IsEqualTo(expected);
}

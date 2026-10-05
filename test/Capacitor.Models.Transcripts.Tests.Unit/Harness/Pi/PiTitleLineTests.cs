using Capacitor.Models.Transcripts.Harness.Pi;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Pi;

public class PiTitleLineTests {
    [Test]
    [Arguments("""{"type":"session_info","name":"x"}""", true)]
    [Arguments("""{"type":"session_info","name":""}""", false)]
    [Arguments("""{"type":"message","text":"session_info"}""", false)]
    public async Task Recognizes_a_named_session_info_record(string line, bool expected) =>
        await Assert.That(PiTitleLine.CarriesTitle(line)).IsEqualTo(expected);
}

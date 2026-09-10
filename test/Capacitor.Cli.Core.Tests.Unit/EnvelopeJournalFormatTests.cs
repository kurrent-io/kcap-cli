namespace Capacitor.Cli.Core.Tests.Unit;

public class EnvelopeJournalFormatTests {
    [Test]
    public async Task Round_trip_pins_the_bytes() {
        var e = new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi", TimestampIso: "2026-09-09T10:00:00.000Z");
        var line = EnvelopeJournalFormat.Write(e);
        await Assert.That(line).StartsWith("""{"contract_version":1,"seq":0,"kind":"user_message","text":"hi",""");
        await Assert.That(line).Contains("\"timestamp_iso\":\"2026-09-09T10:00:00.000Z\"");
        await Assert.That(line).DoesNotContain("\n");
        await Assert.That(EnvelopeJournalFormat.TryRead(line, out var back)).IsTrue();
        await Assert.That(back).IsEqualTo(e);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("42")]
    [Arguments("[]")]
    [Arguments("""{"contract_version":1,"kind":null}""")]
    [Arguments("""{"contract_version":1,"kind":""}""")]
    [Arguments("""{"contract_version":2,"kind":"user_message","text":"x"}""")]
    public async Task Structurally_invalid_lines_are_rejected(string line) =>
        await Assert.That(EnvelopeJournalFormat.TryRead(line, out _)).IsFalse();

    [Test]
    public async Task Unknown_kind_on_a_v1_line_is_accepted() {
        await Assert.That(EnvelopeJournalFormat.TryRead("""{"contract_version":1,"kind":"future_kind"}""", out var e)).IsTrue();
        await Assert.That(e.Kind).IsEqualTo("future_kind");
    }

    [Test]
    public async Task Missing_contract_version_reads_as_v1() =>
        await Assert.That(EnvelopeJournalFormat.TryRead("""{"kind":"assistant_text","text":"a"}""", out _)).IsTrue();
}

using System.Text.Json;
using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Tests.Unit.Harness.GrokBot;

/// <summary>Pins how a gateway entry is read: which entries may wait, and that the line keeps the entry
/// verbatim plus its Bot.</summary>
public class GrokBotEntryTests {
    const string Agent = "4d30ec7f-1027-4a23-86d7-d70f37e5bfec";

    static GrokBotEntry? Parse(string json) {
        using var doc = JsonDocument.Parse(json);
        return GrokBotEntry.TryParse(doc.RootElement, Agent);
    }

    [Test]
    [Arguments("""{"seq":1,"timestampMs":1}""")]
    [Arguments("""{"id":"t1u","timestampMs":1}""")]
    [Arguments("""{"id":"t1u","seq":1}""")]
    [Arguments("""{"id":"t1u","seq":null,"timestampMs":1}""")]
    [Arguments("""{"id":"t1u","seq":1,"timestampMs":"soon"}""")]
    [Arguments("""[]""")]
    public async Task Entries_missing_identity_or_ordering_are_skipped(string json) {
        await Assert.That(Parse(json)).IsNull();
    }

    [Test]
    public async Task Line_is_the_entry_plus_its_agent_id() {
        var entry = Parse("""{"kind":"message","id":"t1u","role":"user","content":"hi","timestampMs":5,"seq":7}""")!;

        using var line = JsonDocument.Parse(entry.Line);
        await Assert.That(line.RootElement.GetProperty("agentId").GetString()).IsEqualTo(Agent);
        await Assert.That(line.RootElement.GetProperty("content").GetString()).IsEqualTo("hi");
        await Assert.That(entry.Seq).IsEqualTo(7);
        await Assert.That(entry.TimestampMs).IsEqualTo(5);
    }

    [Test]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"widget","widget":{}}}""", true)]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"widget","widget":{}},"respondedValue":"x"}""", false)]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"widget","widget":{}},"widgetSkipped":true}""", false)]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"text","content":"x"}}""", false)]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"local-tool-permission","ask":{"action":"read-file"}}}""", true)]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"local-tool-permission","ask":{"action":"read-file","status":"pending"}}}""", true)]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"local-tool-permission","ask":{"action":"read-file","status":"allowed"}}}""", false)]
    [Arguments("""{"kind":"send-message","id":"a","seq":1,"timestampMs":1,"message":{"type":"connector","connector":"Gmail","variant":"connect"}}""", false)]
    public async Task Only_an_unanswered_question_or_permission_ask_awaits(string json, bool open) {
        await Assert.That(Parse(json)!.IsAwaitingAnswer).IsEqualTo(open);
    }

    [Test]
    public async Task Streaming_flag_is_read() {
        await Assert.That(Parse("""{"id":"a","seq":1,"timestampMs":1,"isStreaming":true}""")!.IsStreaming).IsTrue();
        await Assert.That(Parse("""{"id":"a","seq":1,"timestampMs":1,"isStreaming":false}""")!.IsStreaming).IsFalse();
    }
}

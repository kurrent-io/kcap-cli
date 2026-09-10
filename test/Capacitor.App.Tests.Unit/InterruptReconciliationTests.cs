using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class InterruptReconciliationTests {
    static SessionDetailDto Detail(string eventsJson, string? endedAt = null) =>
        JsonSerializer.Deserialize($$"""{"session_id":"s1","ended_at":{{(endedAt is null ? "null" : $"\"{endedAt}\"")}},"last_event_number":9,"events":{{eventsJson}}}""",
            RemoteModelsJsonContext.Default.SessionDetailDto)!;

    [Test]
    public async Task Issued_minus_resolved_is_pending_with_kinds_from_the_extension_blocks() {
        var detail = Detail("""
            [
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash","extensions":{"claude_code":{"permission":{"tool_name":"Bash","tool_input":{"command":"ls"}}}}}},
            {"event_type":"InterruptIssued","event_number":2,"payload":{"request_id":"p2","kind":"permission","tool_name":"fs/write","extensions":{"acp":{"interaction":{"raw_kind":"permission","tool_name":"fs/write","options":[{"label":"Allow","description":null,"option_id":"allow-once"}]}}}}},
            {"event_type":"InterruptIssued","event_number":3,"payload":{"request_id":"q1","kind":"input","tool_name":"AskUserQuestion","prompt":"Pick","extensions":{"acp":{"interaction":{"raw_kind":"elicitation","options":[{"label":"A","option_id":"a","min_selections":1,"max_selections":2},{"label":"A","option_id":"b","min_selections":1,"max_selections":2}],"is_multi_select":true,"min_selections":1,"max_selections":2}}}}},
            {"event_type":"InterruptIssued","event_number":4,"payload":{"request_id":"t1","kind":"input","tool_name":"AskUserQuestion","prompt":"Which?","extensions":{"claude_code":{"elicitation":{"options":[{"label":"X"}]}}}}},
            {"event_type":"InterruptIssued","event_number":5,"payload":{"request_id":"w1","kind":"input","prompt":"idle"}},
            {"event_type":"InterruptResolved","event_number":6,"payload":{"request_id":"p1","outcome":"allow"}}
            ]
            """);
        var r = InterruptReconciliation.FromDetail(detail);
        await Assert.That(r.Ended).IsFalse();
        await Assert.That(r.Pending.Select(p => p.RequestId)).IsEquivalentTo(new[] { "p2", "q1", "t1" });
        var acpPermission = r.Pending.Single(p => p.RequestId == "p2");
        await Assert.That(acpPermission.Kind).IsEqualTo(PendingInterruptKind.AcpPermission);
        await Assert.That(acpPermission.Options[0].OptionId).IsEqualTo("allow-once");
        var question = r.Pending.Single(p => p.RequestId == "q1");
        await Assert.That(question.Kind).IsEqualTo(PendingInterruptKind.AcpQuestion);
        await Assert.That(question.IsMultiSelect).IsTrue();
        await Assert.That(question.MaxSelections).IsEqualTo(2);
        await Assert.That(question.Options.Select(o => o.OptionId)).IsEquivalentTo(new[] { "a", "b" });
        var transcript = r.Pending.Single(p => p.RequestId == "t1");
        await Assert.That(transcript.Kind).IsEqualTo(PendingInterruptKind.TranscriptQuestion);
        await Assert.That(transcript.IsAnswerableOverHttp).IsFalse();
    }

    [Test]
    public async Task Session_end_clears_everything_and_marks_ended() {
        var detail = Detail("""
            [
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}},
            {"event_type":"SessionEnded","event_number":2,"payload":{}}
            ]
            """);
        var r = InterruptReconciliation.FromDetail(detail);
        await Assert.That(r.Pending).IsEmpty();
        await Assert.That(r.Ended).IsTrue();
    }

    [Test]
    public async Task Ended_at_alone_marks_ended_and_data_stands_in_for_a_missing_payload() {
        var detail = Detail("""[{"event_type":"InterruptIssued","event_number":1,"data":{"requestId":"p1","kind":"permission","toolName":"Bash"}}]""", endedAt: "2026-09-10T10:00:00Z");
        var r = InterruptReconciliation.FromDetail(detail);
        await Assert.That(r.Ended).IsTrue();
        await Assert.That(r.Pending).IsEmpty();
    }

    /// An ended detail clears the pending set whatever Body returned, so only a live session
    /// proves that an event carrying `data` instead of `payload` is read at all.
    [Test]
    public async Task Data_stands_in_for_a_missing_payload_on_a_live_session() {
        var detail = Detail("""[{"event_type":"InterruptIssued","event_number":1,"data":{"requestId":"p1","kind":"permission","toolName":"Bash"}}]""");
        var r = InterruptReconciliation.FromDetail(detail);
        await Assert.That(r.Ended).IsFalse();
        await Assert.That(r.Pending.Single().RequestId).IsEqualTo("p1");
        await Assert.That(r.Pending.Single().ToolName).IsEqualTo("Bash");
    }

    [Test]
    public async Task Camel_case_names_are_read_too() {
        var detail = Detail("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"requestId":"p1","kind":"permission","toolName":"Bash"}}]""");
        await Assert.That(InterruptReconciliation.FromDetail(detail).Pending.Single().ToolName).IsEqualTo("Bash");
    }
}

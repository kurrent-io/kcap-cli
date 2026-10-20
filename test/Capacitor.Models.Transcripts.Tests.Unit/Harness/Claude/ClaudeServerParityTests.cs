using Capacitor.Models.Transcripts.Harness.Claude;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

/// <summary>The rules the server's Claude normalizer applies, which the leaf must reproduce before the server
/// can call it: usage and its suppression memory, tool-result text and its raw output, image attachments,
/// and the origin fields on user messages.</summary>
public class ClaudeServerParityTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    const string Uuid  = "a1b2c3d4-0000-4000-8000-000000000001";
    const string Uuid2 = "a1b2c3d4-0000-4000-8000-000000000002";

    static TranscriptContext Ctx() => ClaudeTranscriptEvents.Instance.CreateContext("sess", null);

    static IReadOnlyList<CanonicalEvent> E(string line, TranscriptContext? ctx = null) =>
        ClaudeTranscriptEvents.Instance.Project(line, 1, Received, ctx ?? Ctx()).Events;

    static string Assistant(string uuid, string usage, string blocks = """[{"type":"text","text":"a"},{"type":"text","text":"b"}]""") =>
        $$$"""{"type":"assistant","uuid":"{{{uuid}}}","message":{"model":"claude-x","content":{{{blocks}}}{{{(usage.Length > 0 ? ",\"usage\":" + usage : "")}}}}}""";

    const string U1 = """{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}""";
    const string U2 = """{"input_tokens":11,"output_tokens":5}""";

    [Test]
    public async Task Usage_rides_the_first_event_of_a_record_with_its_cache_creation_count() {
        var e = E(Assistant(Uuid, U1));

        await Assert.That(e).Count().IsEqualTo(2);
        var usage = e[0].Usage!;
        await Assert.That(usage.InputTokens).IsEqualTo(10L);
        await Assert.That(usage.OutputTokens).IsEqualTo(5L);
        await Assert.That(usage.CachedInputTokens).IsEqualTo(3L);
        await Assert.That(usage.TotalTokens).IsEqualTo(20L);
        await Assert.That(usage.Model).IsEqualTo("claude-x");
        await Assert.That(e[0].CacheCreationTokens).IsEqualTo(2L);
        await Assert.That(e[1].Usage).IsNull();
    }

    /// <summary>Claude Code writes each block of one response as its own record with the response's usage
    /// repeated; only a change of tuple, or any record that is not an assistant one, re-arms it.</summary>
    [Test]
    public async Task The_usage_memory_suppresses_a_repeat_and_resets_on_any_other_record() {
        var ctx = Ctx();
        static bool HasUsage(IReadOnlyList<CanonicalEvent> e) => e[0].Usage is not null;

        await Assert.That(HasUsage(E(Assistant(Uuid, U1), ctx))).IsTrue();
        await Assert.That(HasUsage(E(Assistant(Uuid2, U1), ctx))).IsFalse();
        // No usage, and an empty content array, leave the memory as it was.
        await Assert.That(E(Assistant(Uuid, ""), ctx)[0].Usage).IsNull();
        await Assert.That(E(Assistant(Uuid, U2, "[]"), ctx)).IsEmpty();
        await Assert.That(HasUsage(E(Assistant(Uuid2, U1), ctx))).IsFalse();
        // A rejected line touches nothing.
        await Assert.That(ClaudeTranscriptEvents.Instance.Project(Assistant("not-a-guid", U2), 1, Received, ctx).Rejected).IsNotNull();
        await Assert.That(HasUsage(E(Assistant(Uuid2, U1), ctx))).IsFalse();
        await Assert.That(HasUsage(E(Assistant(Uuid, U2), ctx))).IsTrue();
        // Any other valid record, noise included, clears it.
        E("""{"type":"progress"}""", ctx);
        await Assert.That(HasUsage(E(Assistant(Uuid2, U2), ctx))).IsTrue();
    }

    [Test]
    [Arguments("""[{"type":"text","text":"a"},{"type":"image"},{"type":"text","text":"b"}]""", "a\n\nb", true)]
    [Arguments("""[{"type":"image","source":{}}]""", """[{"type":"image","source":{}}]""", true)]
    [Arguments("""{"k":1}""", """{"k":1}""", true)]
    [Arguments("42", "42", true)]
    [Arguments("\"plain\"", "plain", false)]
    public async Task A_tool_result_carries_the_server_text_and_its_raw_output(string content, string text, bool raw) {
        var e = E($$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":{{{content}}}}]}}""");

        await Assert.That(((ToolResultReceived)e[0].Payload).Result).IsEqualTo(text);
        var slug = SchemaExtensions.Slug(e[0].Payload, ClaudeCodeExtension.Slug);
        await Assert.That(slug?.Fields.ContainsKey(ClaudeCodeExtension.OutputRaw) == true).IsEqualTo(raw);
    }

    [Test]
    public async Task An_absent_or_null_result_content_leaves_the_result_unset() {
        foreach (var block in new[] { """{"type":"tool_result","tool_use_id":"t"}""", """{"type":"tool_result","tool_use_id":"t","content":null}""" }) {
            var e = E($$$"""{"type":"user","message":{"content":[{{{block}}}]}}""");
            await Assert.That(((ToolResultReceived)e[0].Payload).HasResult).IsFalse();
        }
    }

    [Test]
    public async Task Each_result_carries_its_own_error_flag() {
        var e = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"a","content":"1"},{"type":"tool_result","tool_use_id":"b","content":"2","is_error":true}]}}""");

        await Assert.That(SchemaExtensions.Slug(e[0].Payload, ClaudeCodeExtension.Slug)).IsNull();
        await Assert.That(SchemaExtensions.Slug(e[1].Payload, ClaudeCodeExtension.Slug)!.Fields[ClaudeCodeExtension.IsError].BoolValue).IsTrue();
    }

    const string Png = "iVBORw0KGgo=";

    [Test]
    public async Task Image_blocks_become_attachments_on_the_user_message_even_without_text() {
        var withText  = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"content":[{"type":"text","text":"look"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{{Png}}}"}},{"type":"image","source":{"type":"base64","media_type":"image/gif","data":"{{{Png}}}"}}]}}""");
        var imageOnly = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"content":[{"type":"image","source":{"type":"base64","media_type":"image/webp","data":"{{{Png}}}"}}]}}""");

        var attachments = withText[0].Attachments!;
        await Assert.That(((UserMessageReceived)withText[0].Payload).Content).IsEqualTo("look");
        await Assert.That(attachments).Count().IsEqualTo(2);
        // Ids count images, not raw blocks: the server's persisted blob streams are keyed by them.
        await Assert.That(attachments[0].Id).IsEqualTo(TranscriptIds.ClaudeAttachment("sess", Guid.Parse(Uuid), 0));
        await Assert.That(attachments[1].Id).IsEqualTo(TranscriptIds.ClaudeAttachment("sess", Guid.Parse(Uuid), 1));
        await Assert.That(attachments[0].FileName).IsEqualTo($"attachment-{attachments[0].Id.ToString()[..8]}.png");
        await Assert.That(attachments[1].ContentType).IsEqualTo("image/gif");
        await Assert.That(attachments[0].Data).IsEquivalentTo(Convert.FromBase64String(Png));

        await Assert.That(((UserMessageReceived)imageOnly[0].Payload).Content).IsEqualTo("");
        await Assert.That(imageOnly[0].Attachments!).Count().IsEqualTo(1);
    }

    [Test]
    public async Task A_queued_prompt_array_carries_its_images_and_is_marked_queued() {
        var e = E($$$"""{"type":"attachment","uuid":"{{{Uuid}}}","attachment":{"type":"queued_command","commandMode":"prompt","prompt":[{"type":"text","text":"see"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"{{{Png}}}"}}]}}""");

        await Assert.That(e[0].Attachments!).Count().IsEqualTo(1);
        var slug = SchemaExtensions.Slug(e[0].Payload, ClaudeCodeExtension.Slug)!;
        await Assert.That(slug.Fields[ClaudeCodeExtension.IsQueued].BoolValue).IsTrue();
        await Assert.That(slug.Fields[ClaudeCodeExtension.CommandMode].StringValue).IsEqualTo("prompt");
    }

    [Test]
    public async Task A_user_message_carries_the_lines_origin_fields() {
        var e = E("""{"type":"user","promptSource":"keyboard","turnPosition":{"promptIndex":3},"origin":{"kind":"peer","handback":true},"message":{"content":"x"}}""");

        var slug = SchemaExtensions.Slug(e[0].Payload, ClaudeCodeExtension.Slug)!;
        await Assert.That(slug.Fields[ClaudeCodeExtension.PromptSource].StringValue).IsEqualTo("keyboard");
        await Assert.That(slug.Fields[ClaudeCodeExtension.PromptIndex].NumberValue).IsEqualTo(3d);
        await Assert.That(slug.Fields[ClaudeCodeExtension.IsHandback].BoolValue).IsTrue();
        await Assert.That(slug.Fields[ClaudeCodeExtension.OriginKind].StringValue).IsEqualTo("peer");
    }

    /// <summary>A queued command's own fields describe who wrote it, as the server reads them from the
    /// attachment rather than the line around it.</summary>
    [Test]
    public async Task A_queued_prompt_carries_the_attachments_own_origin_fields() {
        var e = E("""{"type":"attachment","attachment":{"type":"queued_command","commandMode":"prompt","prompt":"go","isMeta":true,"isCompactSummary":true,"promptSource":"queue","turnPosition":{"promptIndex":4},"origin":{"kind":"human","handback":true}}}""");

        var slug = SchemaExtensions.Slug(e[0].Payload, ClaudeCodeExtension.Slug)!;
        await Assert.That(slug.Fields[ClaudeCodeExtension.IsMeta].BoolValue).IsTrue();
        await Assert.That(slug.Fields[ClaudeCodeExtension.IsCompactSummary].BoolValue).IsTrue();
        await Assert.That(slug.Fields[ClaudeCodeExtension.PromptSource].StringValue).IsEqualTo("queue");
        await Assert.That(slug.Fields[ClaudeCodeExtension.PromptIndex].NumberValue).IsEqualTo(4d);
        await Assert.That(slug.Fields[ClaudeCodeExtension.IsHandback].BoolValue).IsTrue();
    }

    [Test]
    public async Task A_task_notification_with_a_content_array_is_a_user_message() {
        var e = E("""{"type":"attachment","attachment":{"type":"queued_command","commandMode":"task-notification","prompt":[{"type":"text","text":"<task-notification>done</task-notification>"}]}}""");

        await Assert.That(((UserMessageReceived)e[0].Payload).Content).IsEqualTo("<task-notification>done</task-notification>");
        await Assert.That(SchemaExtensions.Slug(e[0].Payload, ClaudeCodeExtension.Slug)!.Fields[ClaudeCodeExtension.OriginKind].StringValue).IsEqualTo("task-notification");
    }
}

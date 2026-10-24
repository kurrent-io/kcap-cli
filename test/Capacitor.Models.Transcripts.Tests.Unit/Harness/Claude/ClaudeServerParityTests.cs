using System.Text.Json.Nodes;
using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf;
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

    /// <summary>Pins the agent schema's canonical shape for the Anthropic extras: every usage key without a canonical
    /// slot rides <c>additional_counts</c> verbatim, nested objects and the service tier string included, while the
    /// flat cache-write total stays where it is.</summary>
    [Test]
    public async Task Usage_carries_every_vendor_usage_key_verbatim_in_additional_counts() {
        const string usage = """{"input_tokens":6,"output_tokens":274,"cache_read_input_tokens":19041,"cache_creation_input_tokens":27055,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":27055},"server_tool_use":{"web_search_requests":2,"web_fetch_requests":1},"service_tier":"priority"}""";
        var e = E(Assistant(Uuid, usage));

        await Assert.That(e[0].Usage!.AdditionalCounts).IsNotNull();
        var counts   = JsonNode.Parse(JsonFormatter.Default.Format(e[0].Usage!.AdditionalCounts));
        var expected = JsonNode.Parse("""{"cache_creation_input_tokens":27055,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":27055},"server_tool_use":{"web_search_requests":2,"web_fetch_requests":1},"service_tier":"priority"}""");
        await Assert.That(JsonNode.DeepEquals(counts, expected)).IsTrue().Because(counts!.ToJsonString());
        await Assert.That(e[0].CacheCreationTokens).IsEqualTo(27055L);
    }

    /// <summary>A vendor's own <c>total_tokens</c> and <c>reasoning_tokens</c> fill their canonical slots, as the agent
    /// schema maps them, so neither is dropped with the other canonical keys; without a vendor total the slot is the
    /// sum of the four counts.</summary>
    [Test]
    [Arguments("""{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":3,"cache_creation_input_tokens":2}""", 20L, null)]
    [Arguments("""{"input_tokens":10,"output_tokens":5,"total_tokens":99}""", 99L, null)]
    [Arguments("""{"input_tokens":10,"output_tokens":5,"reasoning_tokens":4}""", 15L, 4L)]
    public async Task A_vendor_total_and_reasoning_count_fill_their_slots(string usageJson, long total, long? reasoning) {
        var usage = E(Assistant(Uuid, usageJson))[0].Usage!;

        await Assert.That(usage.TotalTokens).IsEqualTo(total);
        await Assert.That(usage.HasReasoningTokens ? usage.ReasoningTokens : (long?)null).IsEqualTo(reasoning);
        await Assert.That(usage.AdditionalCounts?.Fields.ContainsKey("total_tokens") ?? false).IsFalse();
        await Assert.That(usage.AdditionalCounts?.Fields.ContainsKey("reasoning_tokens") ?? false).IsFalse();
    }

    /// <summary>A usage of canonical counts alone carries no extras.</summary>
    [Test]
    public async Task Usage_of_canonical_counts_alone_carries_no_additional_counts() =>
        await Assert.That(E(Assistant(Uuid, U2))[0].Usage!.AdditionalCounts).IsNull();

    /// <summary>A usage that reports only a vendor total, or only fields without a canonical slot, is still usage: it
    /// is carried rather than dropped, and no total is invented for one that reported none.</summary>
    [Test]
    [Arguments("""{"total_tokens":42}""", 42L, null)]
    [Arguments("""{"service_tier":"standard"}""", null, "service_tier")]
    [Arguments("""{"reasoning_tokens":3}""", null, null)]
    public async Task A_sparse_usage_is_kept(string usageJson, long? total, string? extra) {
        var usage = E(Assistant(Uuid, usageJson))[0].Usage;

        await Assert.That(usage).IsNotNull();
        await Assert.That(usage!.HasTotalTokens ? usage.TotalTokens : (long?)null).IsEqualTo(total);
        if (extra is not null) await Assert.That(usage.AdditionalCounts.Fields.ContainsKey(extra)).IsTrue();
    }

    /// <summary>Older Claude Code builds write a growing <c>output_tokens</c> across the blocks of one response, so a block
    /// of the same message id whose usage grew still carries it: keeping only the first snapshot would undercount.</summary>
    [Test]
    public async Task A_grown_snapshot_of_the_same_response_still_carries_its_usage() {
        static string Line(string uuid, int output) =>
            $$"""{"type":"assistant","uuid":"{{uuid}}","message":{"id":"msg_1","model":"claude-x","content":[{"type":"text","text":"a"}],"usage":{"input_tokens":10,"output_tokens":""" + output + "}}}";
        var ctx = Ctx();
        E(Line(Uuid, 5), ctx);

        await Assert.That(E(Line(Uuid2, 120), ctx)[0].Usage?.OutputTokens).IsEqualTo(120L);
    }

    /// <summary>A record repeating the previous record's usage, in any key order, is the same response and carries none;
    /// one whose usage differs in any field, including one with no canonical slot, carries its own.</summary>
    [Test]
    [Arguments("""{"input_tokens":10,"output_tokens":5,"service_tier":"standard"}""", """{"service_tier":"standard","output_tokens":5,"input_tokens":10}""", false)]
    [Arguments("""{"input_tokens":10,"output_tokens":5,"service_tier":"standard"}""", """{"input_tokens":10,"output_tokens":5,"service_tier":"priority"}""", true)]
    [Arguments("""{"input_tokens":10,"output_tokens":5}""", """{"input_tokens":10,"output_tokens":5,"reasoning_tokens":3}""", true)]
    public async Task Only_a_repeat_of_the_whole_usage_is_suppressed(string first, string second, bool secondCarriesUsage) {
        var ctx = Ctx();
        E(Assistant(Uuid, first), ctx);

        await Assert.That(E(Assistant(Uuid2, second), ctx)[0].Usage is not null).IsEqualTo(secondCarriesUsage);
    }

    /// <summary>Claude Code writes each block of one response as its own record with the response's usage
    /// repeated; only a change of usage, or any record that is not an assistant one, re-arms it.</summary>
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

    /// <summary>The server counts text blocks, not text: an array of empty ones is still a message.</summary>
    [Test]
    public async Task An_array_of_empty_text_blocks_is_an_empty_message() {
        var user   = E("""{"type":"user","message":{"content":[{"type":"text","text":""}]}}""");
        var queued = E("""{"type":"attachment","attachment":{"type":"queued_command","commandMode":"prompt","prompt":[{"type":"text","text":null}]}}""");

        await Assert.That(((UserMessageReceived)user[0].Payload).Content).IsEqualTo("");
        await Assert.That(((UserMessageReceived)queued[0].Payload).Content).IsEqualTo("");
    }

    [Test]
    public async Task An_image_with_a_null_media_type_is_an_octet_stream() {
        var e = E($$$"""{"type":"user","message":{"content":[{"type":"image","source":{"type":"base64","media_type":null,"data":"{{{Png}}}"}}]}}""");

        await Assert.That(e[0].Attachments![0].ContentType).IsEqualTo("application/octet-stream");
        await Assert.That(e[0].Attachments![0].FileName).EndsWith(".bin");
    }

    [Test]
    public async Task A_result_the_protobuf_parser_refuses_keeps_its_text_without_a_raw_copy() {
        var e = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":{"n":1e400}}]}}""");

        await Assert.That(((ToolResultReceived)e[0].Payload).Result).IsEqualTo("""{"n":1e400}""");
        await Assert.That(SchemaExtensions.Slug(e[0].Payload, ClaudeCodeExtension.Slug)).IsNull();
    }
}

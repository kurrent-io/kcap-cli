using System.Globalization;
using System.Text.Json;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Tests.Helpers;

/// <summary>A WireMock server answering the routes an evidence-route eval run reads. Lower priority numbers win, so a test
/// overrides an earlier mapping by registering a new one at a lower number.</summary>
public sealed class EvidenceServerStub : IDisposable {
    public const string SessionId = "0123456789abcdef0123456789abcdef";
    public static string RootSource => $"AgentSession-{SessionId}";

    public WireMockServer Server { get; } = WireMockServer.Start();
    public string Url => Server.Url!;
    static string Base => $"/api/sessions/{SessionId}";

    public static string Source(string sourceId, long first, long cutoff, int? turns, string kind = "root", bool available = true) =>
        $$"""{"source_id":"{{sourceId}}","kind":"{{kind}}","session_id":"{{SessionId}}","agent_id":null,"agent_type":null,"parent_source_id":null,"chain_index":0,"depth":0,"revision_cutoff":{{cutoff}},"first_revision":{{first}},"cutoff_anchor":"a","turn_count":{{turns?.ToString(CultureInfo.InvariantCulture) ?? "null"}},"availability":"{{(available ? "available" : "unavailable")}}","discovered_by":"root"}""";

    public static string Manifest(string version, string token, string? next, DateTimeOffset? issued, DateTimeOffset? expires, IEnumerable<string>? sources = null, bool complete = true, IEnumerable<string>? reasons = null) {
        var reasonList = string.Join(",", (reasons ?? []).Select(r => $"\"{r}\""));
        var sourceList = string.Join(",", sources ?? [Source(RootSource, 0, 9, 2)]);
        var nextJson   = next is null ? "null" : $"\"{next}\"";
        var lifetime   = (issued is { } i ? $",\"issued_at\":\"{i:O}\"" : "") + (expires is { } e ? $",\"expires_at\":\"{e:O}\"" : "");
        return $"{{\"scope_version\":\"{version}\",\"root_session_id\":\"{SessionId}\",\"complete\":{(complete ? "true" : "false")},"
             + $"\"incomplete_reasons\":[{reasonList}],\"sources\":[{sourceList}],\"next_cursor\":{nextJson},\"token\":\"{token}\"{lifetime}}}";
    }

    public void FreshScope(string body, int status = 200, int priority = 10) =>
        Server.Given(Request.Create().WithPath($"{Base}/evidence-scope").WithParam("adopted_children", "false").WithParam("continuations", "false").UsingGet())
            .AtPriority(priority).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    public void CursorPage(string cursor, string body, int status = 200, int priority = 10) =>
        Server.Given(Request.Create().WithPath($"{Base}/evidence-scope").WithParam("cursor", cursor).UsingGet())
            .AtPriority(priority).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

    public void Route(string method, string relative, int status, string body, IReadOnlyDictionary<string, string>? query = null, TimeSpan? delay = null, int priority = 10) {
        var request = Request.Create().WithPath($"{Base}/{relative}").UsingMethod(method);
        foreach (var (k, v) in query ?? new Dictionary<string, string>()) request = request.WithParam(k, v);
        var response = Response.Create().WithStatusCode(status).WithBody(body);
        if (delay is { } d) response = response.WithDelay(d);
        Server.Given(request).AtPriority(priority).RespondWith(response);
    }

    /// <summary>The two catalog routes a run reads first; a non-null advertisement enables the evidence route.</summary>
    public void Catalog(string? advertisementJson, string questionsJson, string catalogQuestionsJson) {
        Server.Given(Request.Create().WithPath("/api/eval/questions").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody(questionsJson));
        var ad = advertisementJson is null ? "" : $",\"evidence_retrieval\":{advertisementJson}";
        Server.Given(Request.Create().WithPath("/api/eval/catalog").UsingGet()).RespondWith(Response.Create().WithStatusCode(200)
            .WithBody($$"""{"retrospective_prompt":"Retro {SESSION_META} {VERDICTS_JSON} {TRACE_JSON}","retrospective_prompt_version":"7","questions":{{catalogQuestionsJson}}{{ad}}}"""));
    }

    public IReadOnlyList<ILogEntry> Requests(string relative) =>
        [.. Server.LogEntries.Where(e => e.RequestMessage.Path == (relative.StartsWith('/') ? relative : $"{Base}/{relative}"))];

    public void Dispose() => Server.Stop();

    public static string Quote(string s) => "\"" + JsonEncodedText.Encode(s) + "\"";

    public static string Descriptor(string reference, string field, long bytes, int? ordinal = null) =>
        $"{{\"field\":{Quote(field)},\"ordinal\":{ordinal?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"bytes\":{bytes},\"ref\":{Quote(reference)}}}";

    public static string EventEntry(string source, long revision, string? text, string kind = "assistant_text", bool deferText = false) {
        var r = $"{source}@{revision}";
        var parts = new List<string> {
            $"\"ref\":{Quote(r)}", $"\"revision\":{revision}", "\"event_type\":\"AssistantMessage\"", "\"content_type\":\"application/json\"",
            "\"timestamp\":\"2026-09-23T12:00:00+00:00\"", $"\"kind\":{Quote(kind)}",
            $"\"payload_body\":{Descriptor(r, "payload", 50)}", $"\"metadata_body\":{Descriptor(r, "metadata", 2)}"
        };
        if (deferText) parts.Add($"\"text_body\":{Descriptor(r, "text", 20_000)}");
        else if (text is not null) parts.Add($"\"text\":{Quote(text)}");
        return "{" + string.Join(",", parts) + "}";
    }

    public static string InlineCall(int ordinal, string tool, string argumentsJson) =>
        $"{{\"ordinal\":{ordinal},\"tool\":{Quote(tool)},\"arguments\":{argumentsJson}}}";

    public static string DeferredCall(string source, long revision, int ordinal, string tool) {
        var r = $"{source}@{revision}";
        return $"{{\"ordinal\":{ordinal},\"tool\":{Quote(tool)},\"arguments_body\":{Descriptor(r, "arguments", 40, ordinal)}}}";
    }

    public static string ToolCallEntry(string source, long revision, IEnumerable<string> calls, int? callsTotal = null) {
        var r         = $"{source}@{revision}";
        var callList  = calls.ToList();
        var parts = new List<string> {
            $"\"ref\":{Quote(r)}", $"\"revision\":{revision}", "\"event_type\":\"AssistantToolCallsGenerated\"", "\"content_type\":\"application/json\"",
            "\"timestamp\":\"2026-09-23T12:00:00+00:00\"", "\"kind\":\"tool_call\"",
            $"\"calls\":[{string.Join(",", callList)}]", $"\"calls_total\":{callsTotal ?? callList.Count}",
            $"\"payload_body\":{Descriptor(r, "payload", 50)}", $"\"metadata_body\":{Descriptor(r, "metadata", 2)}"
        };
        return "{" + string.Join(",", parts) + "}";
    }

    public static string EventsPage(string source, IEnumerable<string> entries, string? next = null) {
        var cursor = next is null ? "null" : Quote(next);
        return $"{{\"scope_version\":\"v1\",\"source_id\":{Quote(source)},\"reference\":{Quote(source + "@0")},\"from_revision\":0,\"to_revision\":0,\"budget_bytes\":65536,"
             + $"\"entries\":[{string.Join(",", entries)}],\"supplied\":{{\"items\":1,\"bytes\":null}},\"remaining\":{{\"items\":0,\"bytes\":null}},\"over_budget\":false,\"next_cursor\":{cursor}}}";
    }

    public static string TurnsPage(string source, IEnumerable<(int Index, long Start, long End)> turns, string? next = null) {
        var rows = new List<string>();
        foreach (var (index, start, end) in turns) {
            var turnRef = $"{source}#g1t{index}";
            var events  = $"{source}@{start}-{end}";
            rows.Add($"{{\"turn_ref\":{Quote(turnRef)},\"events_ref\":{Quote(events)},\"range_state\":\"ok\",\"index\":{index},\"start_revision\":{start},\"end_revision\":{end},"
                   + $"\"closed_reason\":\"next_prompt\",\"user_prompt\":\"do it\",\"tool_call_count\":0,\"tool_error_count\":0,\"input_tokens\":1,\"output_tokens\":1,\"total_tokens\":2,"
                   + $"\"content_empty\":false,\"card_body\":{Descriptor(turnRef, "card", 100)}}}");
        }
        var cursor = next is null ? "null" : Quote(next);
        var more   = next is null ? "false" : "true";
        return $"{{\"scope_version\":\"v1\",\"source_id\":{Quote(source)},\"from_index\":0,\"take\":50,\"budget_bytes\":65536,\"turns_available\":true,\"generation\":1,"
             + $"\"turns\":[{string.Join(",", rows)}],\"supplied\":{{\"items\":1,\"bytes\":null}},\"has_more\":{more},\"over_budget\":false,\"next_cursor\":{cursor}}}";
    }

    public static string BodyChunk(string reference, string field, string content, long offset = 0, long? nextOffset = null, string encoding = "utf-8") {
        var next = nextOffset?.ToString(CultureInfo.InvariantCulture) ?? "null";
        return $"{{\"scope_version\":\"v1\",\"reference\":{Quote(reference)},\"field\":{Quote(field)},\"ordinal\":null,\"content_type\":\"text/plain\",\"encoding\":{Quote(encoding)},"
             + $"\"total_bytes\":{offset + content.Length + (nextOffset is null ? 0 : 1)},\"offset\":{offset},\"length\":{content.Length},\"content\":{Quote(content)},\"next_offset\":{next},\"over_budget\":false}}";
    }

    public static string Summary(string indexState = "ready") =>
        $"{{\"scope_version\":\"v1\",\"scope_complete\":true,\"scope_incomplete_reasons\":[],\"index_state\":{Quote(indexState)},\"incomplete_sources\":[],\"incomplete_sources_truncated\":false,"
      + "\"budget_bytes\":65536,\"over_budget\":false,\"totals\":{\"calls\":3},\"by_class\":[],\"authorizations\":{},\"by_tool\":[],\"tools_truncated\":false,\"by_actor\":[],"
      + "\"actors_truncated\":false,\"by_source\":[],\"sources_truncated\":false,\"unknown_tools\":[],\"unknown_tools_truncated\":false,\"repeated_candidates\":[],\"groups_truncated\":false}";
}

using System.Globalization;
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
}

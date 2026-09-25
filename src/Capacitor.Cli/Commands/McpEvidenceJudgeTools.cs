using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Commands;

/// <summary>One evidence-route question's eight judge tools. Each result is the public route's body as a page; every page,
/// call and running total goes to the question's ledger as it happens, so a harness killed mid-question still leaves a
/// consistent ledger. Budgets are charged before a call's arguments are read.</summary>
sealed class McpEvidenceJudgeTools : IDisposable {
    public const string NotExecuted = "not executed: ";
    public const string ScopeMoved  = "scope_moved";

    public static readonly string[] ToolNames = [
        "list_sources", "list_turns", "read_events", "read_body", "list_calls", "summarize_calls", "list_authorizations", "open_page"
    ];

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly EvidenceRunFile    _run;
    readonly EvidenceReadClient _reader;
    readonly JudgeLedgerWriter  _ledger;
    readonly TimeProvider       _time;
    readonly HashSet<string>    _admitted;
    readonly Dictionary<string, JudgeLedgerPage> _pages      = new(StringComparer.Ordinal);
    readonly Dictionary<string, string>          _cites      = new(StringComparer.Ordinal);
    readonly Dictionary<string, string>          _turnEvents = new(StringComparer.Ordinal);
    readonly List<string> _refused = [];
    int     _seq;
    int     _pageNumber;
    int     _charged;
    long    _delivered;
    string? _stop;

    McpEvidenceJudgeTools(EvidenceRunFile run, EvidenceReadClient reader, JudgeLedgerWriter ledger, TimeProvider time) {
        _run      = run;
        _reader   = reader;
        _ledger   = ledger;
        _time     = time;
        _admitted = run.Sources.Select(s => s.SourceId).ToHashSet(StringComparer.Ordinal);
        foreach (var page in run.SeededPages) Keep(page);
    }

    public static McpEvidenceJudgeTools Open(string runPath, HttpClient http, string baseUrl, TimeProvider time) {
        var run = EvidenceRunFile.Read(runPath);
        return new(run, new EvidenceReadClient(http, baseUrl, run.RootSessionId), JudgeLedgerWriter.OpenAppend(run.LedgerPath), time);
    }

    public async Task<(string Text, bool IsError)> CallAsync(string tool, JsonNode? argumentsNode, CancellationToken ct) {
        var seq  = ++_seq;
        var args = argumentsNode?.ToJsonString() ?? "{}";

        if (_stop == ScopeMoved) return Finish(new(seq, tool, args, JudgeLedgerOutcomes.Refused, ScopeMoved, "scope moved", 0), "Error: scope moved", true);
        if (_stop is { } spent) return Finish(new(seq, tool, args, JudgeLedgerOutcomes.NotExecuted, spent, null, 0), NotExecuted + spent, false);
        if (_time.GetUtcNow() >= _run.SoftDeadline) return Stop(seq, tool, args, EvalStopReasons.TimeBudget);
        if (_charged >= _run.Budgets.MaxToolCalls) return Stop(seq, tool, args, EvalStopReasons.ToolCallBudget);
        _charged++;

        try {
            var arguments = argumentsNode switch {
                null           => null,
                JsonObject obj => obj,
                _              => throw new ArgumentException("arguments must be a JSON object")
            };
            return tool switch {
                "list_sources"        => await ListSourcesAsync(seq, args, arguments, ct),
                "open_page"           => await OpenPageAsync(seq, args, arguments, ct),
                "list_turns"          => await ListTurnsAsync(seq, args, arguments, ct),
                "read_events"         => await ReadEventsAsync(seq, args, arguments, ct),
                "read_body"           => await ReadBodyAsync(seq, args, arguments, ct),
                "list_calls"          => await InventoryAsync(seq, tool, args, "evidence-calls", arguments, ["source", "actor", "tool", "class", "status"], ["path", "path_prefix", "q", "from", "to"], ct),
                "summarize_calls"     => await InventoryAsync(seq, tool, args, "evidence-calls/summary", arguments, ["source", "actor"], [], ct),
                "list_authorizations" => await InventoryAsync(seq, tool, args, "evidence-authorizations", arguments, ["source", "outcome"], [], ct),
                _                     => throw new ArgumentException($"unknown tool '{tool}'")
            };
        } catch (ArgumentException e) {
            return Finish(new(seq, tool, args, JudgeLedgerOutcomes.Error, null, e.Message, 0), $"Error: {e.Message}", true);
        } catch (Exception e) when (!IsFatal(e) && !(e is OperationCanceledException && ct.IsCancellationRequested)) {
            // An exception that escapes here ends the judge's MCP server, and with it every later call of the question.
            return Finish(new(seq, tool, args, JudgeLedgerOutcomes.Error, null, e.GetType().Name, 0), $"Error: the call failed ({e.GetType().Name}: {e.Message}); retry", true);
        }
    }

    static bool IsFatal(Exception e) => e is OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException;

    public static McpTool[] ToolsList() {
        static McpSchemaProperty S(string description) => new("string", description);
        static McpSchemaProperty A(string description) => new("array", description, new McpSchemaProperty("string", "one value"));
        static McpInputSchema Schema(Dictionary<string, McpSchemaProperty> properties, params string[] required) => new("object", properties, required);
        return [
            new("list_sources", "The admitted sources — this session and its subagent lanes — with revision ranges and turn counts, 50 per page.",
                Schema(new() { ["from"] = new("integer", "Row to start from; the previous page's next_from.") }), McpToolAnnotations.Read),
            new("list_turns", "A source's turn outline. Each turn row carries its turn ref, its event range and a cite handle.",
                Schema(new() { ["source"] = S("A source id from list_sources."), ["from_index"] = new("integer", "First turn index; default 0.") }, "source"), McpToolAnnotations.Read),
            new("read_events", "The events of a ref (<source>@<rev>, <source>@<from>-<to>, a turn ref) or of a cite handle from a page you were shown.",
                Schema(new() { ["ref"] = S("An evidence ref or cite handle.") }, "ref"), McpToolAnnotations.Read),
            new("read_body", "A text, output, arguments, call or payload body an event left as a descriptor.",
                Schema(new() {
                    ["ref"] = S("The event ref the descriptor names."), ["field"] = S("text, output, arguments, call or payload."),
                    ["ordinal"] = new("integer", "The call ordinal for arguments and call bodies."), ["offset"] = new("integer", "Byte offset; default 0.")
                }, "ref", "field"), McpToolAnnotations.Read),
            new("list_calls", "The tool-call inventory across the admitted sources, filtered.",
                Schema(new() {
                    ["source"] = A("Source ids."), ["actor"] = A("Actor ids."), ["tool"] = A("Tool names."), ["class"] = A("Call classes."), ["status"] = A("Result statuses."),
                    ["path"] = S("An exact path."), ["path_prefix"] = S("A path prefix."), ["q"] = S("Text to search for."),
                    ["from"] = S("Earliest issue time, ISO 8601."), ["to"] = S("Latest issue time, ISO 8601."), ["include_turns"] = new("boolean", "Attach each call's turn ref.")
                }), McpToolAnnotations.Read),
            new("summarize_calls", "Call counts by class, tool, actor and source.",
                Schema(new() { ["source"] = A("Source ids."), ["actor"] = A("Actor ids.") }), McpToolAnnotations.Read),
            new("list_authorizations", "Permission requests and their outcomes.",
                Schema(new() { ["source"] = A("Source ids."), ["outcome"] = A("Outcomes.") }), McpToolAnnotations.Read),
            new("open_page", "Re-open a page you were shown by its handle; with next true, fetch its continuation under a new handle.",
                Schema(new() { ["page"] = S("A page handle such as p3 or o1."), ["next"] = new("boolean", "Fetch the page's continuation.") }, "page"), McpToolAnnotations.Read)
        ];
    }

    public void Dispose() => _ledger.Dispose();

    async Task<(string, bool)> ListSourcesAsync(int seq, string args, JsonObject? arguments, CancellationToken ct) {
        if (await ReopenAsync(seq, "list_sources", args, ct) is { } refused) return refused;
        var from = (int)Math.Clamp(Long(arguments, "from") ?? 0, 0, int.MaxValue);
        return Accept(EvidencePageRenderer.RenderSources(seq, JudgeCiteHandles.Page(_pageNumber + 1), args, _run.Sources, from), seq, "list_sources", args);
    }

    async Task<(string, bool)> OpenPageAsync(int seq, string args, JsonObject? arguments, CancellationToken ct) {
        var handle = Required(arguments, "page");
        if (!_pages.TryGetValue(handle, out var page)) throw new ArgumentException($"no page '{handle}' was shown to you");
        if (await ReopenAsync(seq, "open_page", args, ct) is { } refused) return refused;

        if (Bool(arguments, "next") != true) {
            if (_delivered + page.Bytes > _run.Budgets.JudgeByteBudgetBytes) return Stop(seq, "open_page", args, EvalStopReasons.ByteBudget);
            _delivered += page.Bytes;
            return Finish(new(seq, "open_page", args, JudgeLedgerOutcomes.Executed, null, null, page.Bytes), page.Text, false);
        }

        if (page.Tool == "list_sources") throw new ArgumentException("list_sources continues through its from argument");
        if (!page.HasNext || page.Next is not { } next) throw new ArgumentException($"page '{handle}' has no continuation");

        var pageArgs = "{\"page\":\"" + JsonEncodedText.Encode(handle) + "\",\"next\":true}";
        EvidenceHttpResult result;
        if (page.Tool == "read_body") {
            if (page.Bodies.Count == 0) throw new ArgumentException($"page '{handle}' names no body to continue");
            var (bodyRef, field, ordinal) = page.Bodies[0];
            List<(string Key, string Value)> query = [("token", _run.Token), ("ref", bodyRef), ("field", field), ("offset", next), ("max_bytes", PageBudget)];
            if (ordinal is { } o) query.Add(("ordinal", o.ToString(Inv)));
            result = await _reader.GetAsync("evidence-body", query, ct);
        } else {
            result = await _reader.GetAsync(RouteOf(page.Tool), [("cursor", next)], ct);
        }
        return await AnswerAsync(seq, "open_page", args, page.Tool, pageArgs, result, page.Source, ct);
    }

    async Task<(string, bool)> ListTurnsAsync(int seq, string args, JsonObject? arguments, CancellationToken ct) {
        var source = Admitted(Required(arguments, "source"));
        List<(string Key, string Value)> query = [("token", _run.Token), ("source", source), ("budget_bytes", PageBudget)];
        if (Long(arguments, "from_index") is { } fromIndex) query.Add(("from_index", fromIndex.ToString(Inv)));
        return await AnswerAsync(seq, "list_turns", args, "list_turns", args, await _reader.GetAsync("evidence-turns", query, ct), source, ct);
    }

    async Task<(string, bool)> ReadEventsAsync(int seq, string args, JsonObject? arguments, CancellationToken ct) {
        var (reference, source) = Resolve(Required(arguments, "ref"));
        if (_turnEvents.TryGetValue(reference, out var events)) reference = events;
        var result = await _reader.GetAsync("evidence-events", [("token", _run.Token), ("ref", reference), ("budget_bytes", PageBudget)], ct);
        return await AnswerAsync(seq, "read_events", args, "read_events", args, result, source, ct);
    }

    async Task<(string, bool)> ReadBodyAsync(int seq, string args, JsonObject? arguments, CancellationToken ct) {
        var (reference, source) = Resolve(Required(arguments, "ref"));
        List<(string Key, string Value)> query = [
            ("token", _run.Token), ("ref", reference), ("field", Required(arguments, "field")),
            ("offset", (Long(arguments, "offset") ?? 0).ToString(Inv)), ("max_bytes", PageBudget)
        ];
        if (Long(arguments, "ordinal") is { } ordinal) query.Add(("ordinal", ordinal.ToString(Inv)));
        return await AnswerAsync(seq, "read_body", args, "read_body", args, await _reader.GetAsync("evidence-body", query, ct), source, ct);
    }

    async Task<(string, bool)> InventoryAsync(int seq, string tool, string args, string route, JsonObject? arguments, string[] arrays, string[] scalars, CancellationToken ct) {
        List<(string Key, string Value)> query = [("token", _run.Token), ("budget_bytes", PageBudget)];
        string? single = null;
        foreach (var name in arrays) {
            var values = Strings(arguments, name);
            if (name == "source") {
                foreach (var s in values) Admitted(s);
                single = values.Count == 1 ? values[0] : null;
            }
            query.AddRange(values.Select(v => (name, v)));
        }
        foreach (var name in scalars)
            if (Str(arguments, name) is { } value) query.Add((name, value));
        if (tool == "list_calls" && Bool(arguments, "include_turns") is { } includeTurns) query.Add(("include_turns", includeTurns ? "true" : "false"));
        return await AnswerAsync(seq, tool, args, tool, args, await _reader.GetAsync(route, query, ct), single, ct);
    }

    async Task<(string, bool)> AnswerAsync(int seq, string callTool, string callArgs, string pageTool, string pageArgs, EvidenceHttpResult result, string? source, CancellationToken ct) {
        switch (result.Status) {
            case >= 200 and < 300:
                JudgeLedgerPage page;
                try { page = EvidencePageRenderer.Render(seq, JudgeCiteHandles.Page(_pageNumber + 1), pageTool, pageArgs, result.Body); }
                catch (Exception e) when (e is JsonException or InvalidOperationException) { return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Error, null, "unreadable page", 0), "Error: the server returned an unreadable page", true); }
                return Accept(page, seq, callTool, callArgs);
            case 400:
                var (code, detail) = ReadError(result.Body);
                return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Error, null, code, 0), $"Error: {code} — {detail}", true);
            case 404:
                var reopen = await _reader.ReopenScopeAsync(_run.Token, ct);
                if (reopen.Status is 404 or 409) return MoveScope(seq, callTool, callArgs);
                if (!reopen.IsSuccess) return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Error, null, "scope re-check failed", 0), "Error: could not re-check the bound scope; retry", true);
                if (source is not null && !_refused.Contains(source)) _refused.Add(source);
                return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Refused, null, "not in the bound scope", 0), "Error: not in the bound scope", true);
            case 409:
                return MoveScope(seq, callTool, callArgs);
            case 503:
                return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Error, null, "index_busy", 0), "Error: index busy; retry", true);
            case 401:
                return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Error, null, "unauthorized", 0), "Error: authentication failed — run 'kcap login' to re-authenticate", true);
            case 0:
                return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Error, null, "unreachable", 0), $"Error: server unreachable: {result.Body}", true);
            default:
                return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Error, null, $"http {result.Status}", 0), $"Error: HTTP {result.Status}", true);
        }
    }

    (string, bool) Accept(JudgeLedgerPage page, int seq, string callTool, string callArgs) {
        if (_delivered + page.Bytes > _run.Budgets.JudgeByteBudgetBytes) return Stop(seq, callTool, callArgs, EvalStopReasons.ByteBudget);
        _pageNumber++;
        Keep(page);
        _ledger.Append(page);
        _delivered += page.Bytes;
        return Finish(new(seq, callTool, callArgs, JudgeLedgerOutcomes.Executed, null, null, page.Bytes), page.Text, false);
    }

    async Task<(string, bool)?> ReopenAsync(int seq, string tool, string args, CancellationToken ct) {
        var reopen = await _reader.ReopenScopeAsync(_run.Token, ct);
        if (reopen.Status is 404 or 409) return MoveScope(seq, tool, args);
        if (!reopen.IsSuccess) return Finish(new(seq, tool, args, JudgeLedgerOutcomes.Error, null, "scope re-check failed", 0), "Error: could not re-check the bound scope; retry", true);
        return null;
    }

    (string, bool) MoveScope(int seq, string tool, string args) {
        _stop = ScopeMoved;
        return Finish(new(seq, tool, args, JudgeLedgerOutcomes.Refused, ScopeMoved, "scope moved", 0), "Error: scope moved", true);
    }

    (string, bool) Stop(int seq, string tool, string args, string reason) {
        _stop = reason;
        return Finish(new(seq, tool, args, JudgeLedgerOutcomes.NotExecuted, reason, null, 0), NotExecuted + reason, false);
    }

    (string, bool) Finish(JudgeLedgerCall call, string text, bool isError) {
        _ledger.Append(call);
        _ledger.Append(new JudgeLedgerFooter(_charged, _delivered, _stop, [.. _refused], _time.GetUtcNow()));
        return (text, isError);
    }

    void Keep(JudgeLedgerPage page) {
        _pages[page.Handle] = page;
        foreach (var (cite, reference) in page.Cites) _cites[cite] = reference;
        if (page.Tool != "list_turns") return;
        using var doc = JsonDocument.Parse(page.Text);
        if (doc.RootElement.Arr("turns") is not { } turns) return;
        foreach (var t in turns.EnumerateArray())
            if (t.Str("turn_ref") is { } turnRef && t.Str("events_ref") is { } eventsRef) _turnEvents[turnRef] = eventsRef;
    }

    (string Reference, string Source) Resolve(string token) {
        var reference = _cites.TryGetValue(token, out var cited) ? cited : token;
        if (!EvidenceRefText.TryParse(reference, out var parsed))
            throw new ArgumentException($"'{token}' is neither an evidence ref nor a cite handle from a page you were shown");
        return (reference, Admitted(parsed.SourceId));
    }

    string Admitted(string source) => _admitted.Contains(source)
        ? source
        : throw new ArgumentException($"'{source}' is not in the admitted scope ({_admitted.Count} sources; list_sources lists them)");

    string PageBudget => _run.Budgets.PageBudgetBytes.ToString(Inv);

    static string RouteOf(string tool) => tool switch {
        "list_turns"          => "evidence-turns",
        "read_events"         => "evidence-events",
        "list_calls"          => "evidence-calls",
        "list_authorizations" => "evidence-authorizations",
        _                     => throw new ArgumentException($"a {tool} page has no continuation")
    };

    static (string Code, string Detail) ReadError(string body) {
        try {
            using var doc = JsonDocument.Parse(body);
            return (doc.RootElement.Str("code") ?? "bad_request", doc.RootElement.Str("detail") ?? "");
        } catch (JsonException) {
            return ("bad_request", body);
        }
    }

    static string Required(JsonObject? args, string name) => Str(args, name) ?? throw new ArgumentException($"missing required argument: {name}");

    static string? Str(JsonObject? args, string name) => args?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool? Bool(JsonObject? args, string name) => args?[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static long? Long(JsonObject? args, string name) => args?[name] is not JsonValue v ? null
        : v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<double>(out var d) && d == Math.Floor(d) ? (long)d
        : throw new ArgumentException($"{name} must be an integer");

    static IReadOnlyList<string> Strings(JsonObject? args, string name) => args?[name] switch {
        null                                              => [],
        JsonArray array                                   => [.. array.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException($"{name} must be an array of strings"))],
        JsonValue value when value.TryGetValue<string>(out var single) => [single],
        _                                                 => throw new ArgumentException($"{name} must be an array of strings")
    };
}

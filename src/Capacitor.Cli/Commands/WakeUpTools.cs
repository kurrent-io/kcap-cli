using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Commands;

/// <summary>The wake-up tools: a hosted agent schedules a prompt to be delivered to itself later, lists its session's
/// wake-ups and cancels one. Each is one request; nothing here waits for a wake-up to fire.</summary>
static partial class WakeUpTools {
    internal const string Schedule = "schedule_wakeup";
    internal const string List     = "list_wakeups";
    internal const string Cancel   = "cancel_wakeup";

    internal const string Route = "/api/wakeups";

    /// <summary>The flows client has no deadline of its own, and a lost answer must not hold the serial tool loop.</summary>
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    internal static bool Handles(string toolName) => toolName is Schedule or List or Cancel;

    internal static IEnumerable<McpTool> Describe() => [
        new(Schedule,
            "Schedule a wake-up for this hosted agent: after the delay, kcap delivers the prompt to you as your next input, waking you if you are idle. " +
            "Use it when the next step is due later, with nothing to do in between, such as reading CI and the reviewers' findings after a push. " +
            "After scheduling, END YOUR TURN: do not wait, sleep or poll. " +
            "Give exactly one of delay_minutes and at; a wake-up fires between 1 minute and 7 days from now. " +
            "The prompt is delivered exactly as written, so name what to check; the task itself is already in your context. " +
            "Only a running hosted agent can be woken: a terminal session is refused and should use its harness's own scheduling. A flow participant is refused too. " +
            "A wake-up is not re-sent: if it ends unconfirmed or dropped, list_wakeups shows it, and you decide whether to schedule again.",
            new("object", new() {
                ["delay_minutes"] = new("integer", "Minutes from now until the wake-up fires."),
                ["at"]            = new("string", "When it fires, as a date and time with an offset, such as 2026-10-24T15:00:00Z."),
                ["prompt"]        = new("string", "What you will receive, at most 4 KB of UTF-8."),
                ["session_id"]    = new("string", "Optional. The calling session. Defaults to the session this server runs in.")
            }, ["prompt"]),
            McpToolAnnotations.Additive),
        new(List,
            "List this session's wake-ups: those pending or being delivered, and those settled in the last 24 hours with how they ended. " +
            "State is pending, claimed, sent, unconfirmed, confirmed, cancelled or dropped; a dropped one carries its reason as outcome.",
            new("object", new() {
                ["session_id"] = new("string", "Optional. The calling session. Defaults to the session this server runs in.")
            }, []),
            McpToolAnnotations.Read),
        new(Cancel,
            "Cancel a pending wake-up of this session. One already being delivered can no longer be cancelled.",
            new("object", new() {
                ["wakeup_id"]  = new("string", "The id schedule_wakeup or list_wakeups returned."),
                ["session_id"] = new("string", "Optional. The calling session. Defaults to the session this server runs in.")
            }, ["wakeup_id"]),
            McpToolAnnotations.Destructive)
    ];

    internal static Task<(HttpResponseMessage? Response, string How)> SendAsync(
            string toolName, JsonObject? arguments, HttpClient client, string apiRoot, string? ambientSessionId, string? callerAgentId,
            FlowRetryClock clock) {
        var sessionId = McpSessionId.ResolveWithin(arguments, ambientSessionId);

        return toolName switch {
            Schedule => BoundedAsync(ct => client.PostAsync(apiRoot + Route, Json(ScheduleBody(arguments, sessionId, callerAgentId)), ct), clock),
            List     => BoundedAsync(ct => client.GetAsync($"{apiRoot}{Route}?session_id={Uri.EscapeDataString(sessionId)}", ct), clock),
            _        => BoundedAsync(ct => client.PostAsync(
                $"{apiRoot}{Route}/{WakeUpId(arguments)}/cancel", Json(new JsonObject { ["session_id"] = sessionId }), ct), clock)
        };
    }

    static JsonObject ScheduleBody(JsonObject? arguments, string sessionId, string? callerAgentId) {
        var prompt   = McpToolArguments.RequireString(arguments, "prompt");
        var hasDelay = McpToolArguments.TryReadInt(arguments, "delay_minutes", out var minutes);
        var at       = McpToolArguments.OptionalString(arguments, "at");

        if (hasDelay == (at is not null)) throw new ArgumentException("Give exactly one of delay_minutes and at.");

        var body = new JsonObject { ["session_id"] = sessionId, ["prompt"] = prompt, ["caller_agent_id"] = callerAgentId };

        if (hasDelay) body["delay_seconds"] = minutes * 60L;
        else body["fire_at"] = ParseAt(at!).ToString("O", CultureInfo.InvariantCulture);

        return body;
    }

    // An offset is required: a bare local time would fire at a moment that depends on the server's clock zone.
    static DateTimeOffset ParseAt(string at) =>
        ExplicitOffset().IsMatch(at) && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : throw new ArgumentException("'at' must be a date and time with an offset, such as 2026-10-24T15:00:00Z.");

    [GeneratedRegex(@"(?:[Zz]|[+-]\d{2}:?\d{2})$")]
    private static partial Regex ExplicitOffset();

    static string WakeUpId(JsonObject? arguments) =>
        Guid.TryParse(McpToolArguments.RequireString(arguments, "wakeup_id"), out var id)
            ? id.ToString("D")
            : throw new ArgumentException("'wakeup_id' must be a wake-up id as schedule_wakeup or list_wakeups returned it.");

    static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    static async Task<(HttpResponseMessage? Response, string How)> BoundedAsync(
            Func<CancellationToken, Task<HttpResponseMessage>> send, FlowRetryClock clock) {
        using var timeout = clock.CreateTimeoutSource(RequestTimeout);

        try {
            return (await send(timeout.Token), "");
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
            return (null, ex is OperationCanceledException ? $"timed out after {(int)RequestTimeout.TotalSeconds} s" : $"failed: {ex.Message}");
        }
    }

    internal static string Unanswered(string toolName, string how) => toolName switch {
        Schedule => $"Error: the request to schedule the wake-up {how}. It may or may not have been scheduled: call list_wakeups before scheduling again.",
        Cancel   => $"Error: the request to cancel the wake-up {how}. Call list_wakeups to see whether it was cancelled.",
        _        => $"Error: listing wake-ups {how}. Retry the call."
    };

    internal static (string Text, bool IsError) Render(string toolName, int status, string body, JsonObject? arguments) {
        using var document = TryParse(body);
        var root = document?.RootElement ?? default;
        var code = root.Str("error") is { Length: > 0 } stated ? stated : null;

        if (status is >= 200 and < 300) return Success(toolName, root, arguments);

        // With the feature off nothing is mapped under the route: a POST meets the page fallback's 405, a GET a 404.
        if (status is (404 or 405) && code is null)
            return ($"Error: this server does not support wake-ups: {Route} answered {status} with no refusal code, so the route is missing or switched off. Nothing was scheduled or changed.", true);

        if (code is null) return ($"Error: HTTP {status} — {body}{UnknownOutcome(toolName, status)}", true);

        return ($"Error ({code}){(root.Str("message") is { Length: > 0 } message ? $": {message}" : "")}", true);
    }

    // A coded refusal says what happened; an uncoded server failure on a change does not say whether it was committed.
    static string UnknownOutcome(string toolName, int status) => (toolName, status >= 500) switch {
        (Schedule, true) => "\nThe wake-up may or may not have been scheduled: call list_wakeups before scheduling again.",
        (Cancel, true)   => "\nWhether the wake-up was cancelled is unknown: call list_wakeups before cancelling again.",
        _                => ""
    };

    static readonly string[] ListedFields = ["wakeup_id", "state", "outcome", "fire_at", "scheduled_at", "settled_at", "prompt"];

    static (string Text, bool IsError) Success(string toolName, JsonElement root, JsonObject? arguments) {
        switch (toolName) {
            case Schedule when root.Str("wakeup_id") is { } id && root.Str("fire_at") is { } fireAt:
                return ($"wakeup_id: {id}\nfire_at: {fireAt}\nScheduled. End your turn now; the prompt arrives as your next input.", false);
            case Schedule:
                return ($"Error: the server's answer to POST {Route} could not be read. Call list_wakeups to see whether the wake-up was scheduled.", true);
            case Cancel when root.Bool("ok") == true:
                return ($"Cancelled wake-up {WakeUpId(arguments)}.", false);
            case Cancel:
                return ($"Error: the server's answer to the cancel of wake-up {WakeUpId(arguments)} could not be read, so whether it was cancelled is unknown. Call list_wakeups to see.", true);
        }

        var unreadable = ($"Error: the server's wake-up list (GET {Route}) could not be read.", true);

        if (root.Arr("wakeups") is not { } rows) return unreadable;

        var blocks = new List<string>();

        foreach (var row in rows.EnumerateArray()) {
            if (row.Str("wakeup_id") is null || row.Str("state") is null) return unreadable;

            blocks.Add(string.Join('\n', ListedFields.Select(f => row.Str(f) is { } value ? $"{f}: {value}" : null).OfType<string>()));
        }

        return blocks.Count == 0
            ? ("This session has no wake-ups pending, and none settled in the last 24 hours.", false)
            : (string.Join("\n\n", blocks), false);
    }

    static JsonDocument? TryParse(string body) {
        try {
            return JsonDocument.Parse(body);
        } catch (JsonException) {
            return null;
        }
    }
}

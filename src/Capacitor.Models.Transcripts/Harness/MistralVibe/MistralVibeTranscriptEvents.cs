using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.MistralVibe;

public sealed class MistralVibeContext : TranscriptContext { }

/// <summary>
/// Projects Mistral Vibe's transcript into canonical events. One line in, events out — the caller
/// feeds it both of Vibe's on-disk shapes:
/// <list type="bullet">
///   <item>Legacy <c>messages.jsonl</c>: one OpenAI chat message per line
///   (<c>role</c>/<c>content</c>/<c>tool_calls</c>/<c>tool_call_id</c>).</item>
///   <item>Unified store: one <c>public-session-state</c> entry per line, flattened out of the
///   generation chunks + journal by the reader, discriminated by a <c>type</c> field
///   (<c>message</c>/<c>reasoning</c>/<c>effect</c>/<c>notice</c>/<c>checkpoint</c>).</item>
/// </list>
/// A line is unified when its top-level <c>type</c> is one of those tokens, otherwise it is read as a
/// legacy message by its <c>role</c>.
///
/// <para>Unified entries are camelCase. An <c>effect</c> is one tool call: <c>detail</c> holds the
/// qualified <c>toolName</c>, its <c>kind</c> and <c>input</c>; <c>state</c> holds the settled
/// <c>status</c> with <c>outputText</c>, or an <c>error</c> on failure. Reads are defensive — an
/// unreadable entry degrades to <see cref="ProjectionResult.Empty"/>, never a crash.</para>
/// </summary>
public sealed class MistralVibeTranscriptEvents : ITranscriptProjection {
    public static readonly MistralVibeTranscriptEvents Instance = new();

    MistralVibeTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new MistralVibeContext();

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");

            var (at, recordTimestamp) = ResolveTimestamp(root, receivedAt);
            var ts       = Timestamp.FromDateTimeOffset(at);
            // A unified-store entry carries a stable id (dedups across journal→chunk and re-import); a
            // legacy messages.jsonl line has none, so it falls back to the line hash.
            var recordId = root.Str("id") is { } entryId ? TranscriptIds.VibeEntry(entryId) : TranscriptIds.VibeRecord(line);

            return root.Str("type") switch {
                "message"    => Legacy(root, recordId, at, recordTimestamp, ts),
                "reasoning"  => One(Reasoning(root, ts), recordId, at, recordTimestamp),
                "effect"     => Effect(root, recordId, at, recordTimestamp, ts),
                "notice" or "checkpoint" => ProjectionResult.Empty,
                _            => Legacy(root, recordId, at, recordTimestamp, ts),
            };
        }
    }

    // ── legacy messages.jsonl (and a unified "message" entry, same chat shape) ──────────────────

    static ProjectionResult Legacy(JsonElement msg, Guid recordId, DateTimeOffset at, string? recordTs, Timestamp ts) {
        var events = new List<CanonicalEvent>();

        switch (msg.Str("role")) {
            case "user": {
                var text = ContentText(msg);
                if (text.Length > 0) Add(events, new UserMessageReceived { Content = text, Timestamp = ts }, recordId, at, recordTs);
                break;
            }
            case "assistant": {
                var text = ContentText(msg);
                if (text.Length > 0) Add(events, new AssistantTextGenerated { Content = text, Timestamp = ts }, recordId, at, recordTs);
                if (msg.Arr("tool_calls") is { } calls && ToolCalls(calls, ts) is { } toolCall)
                    Add(events, toolCall, TranscriptIds.Sibling(recordId, "tool_calls"), at, recordTs);
                break;
            }
            case "tool": {
                var callId = msg.Str("tool_call_id") ?? msg.Str("call_id") ?? "";
                Add(events, new ToolResultReceived { CallId = callId, Result = ContentText(msg), Timestamp = ts }, recordId, at, recordTs);
                break;
            }
            // "system" and anything else is not conversation we record.
        }

        return events.Count == 0 ? ProjectionResult.Empty : ProjectionResult.Of(events);
    }

    // ── unified "effect": one tool call, carrying its own settled result ────────────────────────

    static ProjectionResult Effect(JsonElement effect, Guid recordId, DateTimeOffset at, string? recordTs, Timestamp ts) {
        if (effect.Obj("detail") is not { } detail) return ProjectionResult.Empty;
        var toolName = ToolName(detail.Str("toolName") ?? effect.Str("title") ?? "");
        if (toolName.Length == 0) return ProjectionResult.Empty;

        // The entry id is the call's identity; its result rides on the same entry, so nothing else
        // has to pair with it.
        var callId    = effect.Str("id") ?? recordId.ToString();
        var arguments = detail.Obj("input") is { } input ? StructOf(input) : new Struct();

        var toolCall = new AssistantToolCallsGenerated { Timestamp = ts };
        toolCall.ToolCalls.Add(new ToolCallInfo {
            CallId = callId, ToolName = toolName, Arguments = arguments,
            ToolKind = MistralVibeToolKinds.Of(detail.Str("kind"), toolName),
        });

        var events = new List<CanonicalEvent> {
            new(CanonicalEventTypes.Of(toolCall), toolCall, recordId, at, recordTs),
        };

        if (effect.Obj("state") is { } state && EffectResultText(state) is { } resultText) {
            var result = new ToolResultReceived { CallId = callId, Result = resultText, Timestamp = ts };
            events.Add(new CanonicalEvent(CanonicalEventTypes.Of(result), result, TranscriptIds.Sibling(recordId, "result"), at, recordTs));
        }

        return ProjectionResult.Of(events);
    }

    /// The settled text of an effect's <c>state</c>, or null while it is still pending or running.
    static string? EffectResultText(JsonElement state) {
        var outputText = state.Str("outputText") is { Length: > 0 } text ? text : null;

        return state.Str("status") switch {
            "completed"             => outputText ?? (state.TryGetProperty("output", out var output) ? OutputText(output) : ""),
            "failed"                => string.Join('\n', new[] { state.Obj("error")?.Str("message"), outputText }.OfType<string>()),
            "cancelled" or "skipped" => state.Str("reason") ?? outputText ?? "",
            _                       => null,
        };
    }

    /// A tool's <c>output</c> when Vibe left <c>outputText</c> empty: MCP-style text blocks, else the
    /// <c>structured_content</c> its built-in tools return (<c>stdout</c>/<c>stderr</c> from a shell,
    /// <c>content</c> from a read), else the raw JSON.
    static string OutputText(JsonElement output) {
        if (output.ValueKind == JsonValueKind.String) return output.GetString() ?? "";
        if (output.ValueKind != JsonValueKind.Object) return output.ValueKind == JsonValueKind.Null ? "" : output.GetRawText();

        if (output.Arr("content") is { } blocks && JoinTextBlocks(blocks, "text") is { Length: > 0 } text) return text;

        if (output.Obj("structured_content") is { } structured) {
            if (structured.Str("stdout") is { } stdout)
                return structured.Str("stderr") is { Length: > 0 } stderr ? $"{stdout}\n{stderr}" : stdout;
            if (structured.Str("content") is { } content) return content;
            return structured.GetRawText();
        }

        return output.GetRawText();
    }

    static AssistantThinkingGenerated Reasoning(JsonElement entry, Timestamp ts) {
        var content = ContentText(entry);
        return new AssistantThinkingGenerated {
            Content   = content,
            Encrypted = content.Length == 0 && (entry.Str("encrypted_content") is not null || entry.Bool("encrypted") == true),
            Timestamp = ts,
        };
    }

    // ── shared helpers ──────────────────────────────────────────────────────────────────────────

    static AssistantToolCallsGenerated? ToolCalls(JsonElement toolCalls, Timestamp ts) {
        var call = new AssistantToolCallsGenerated { Timestamp = ts };
        foreach (var tc in toolCalls.EnumerateArray()) {
            var fn      = tc.Obj("function");
            var name    = ToolName(fn?.Str("name") ?? tc.Str("name") ?? "");
            if (name.Length == 0) continue;
            var rawArgs = fn?.Str("arguments") ?? tc.Str("arguments");
            var callId  = tc.Str("id") ?? tc.Str("call_id") ?? "";
            call.ToolCalls.Add(Info(callId, name, ArgumentsStruct(rawArgs)));
        }
        return call.ToolCalls.Count == 0 ? null : call;
    }

    static ToolCallInfo Info(string callId, string toolName, Struct arguments) => new() {
        CallId = callId, ToolName = toolName, Arguments = arguments, ToolKind = MistralVibeToolKinds.Of(null, toolName),
    };

    /// Content is a plain string, or an array of typed text blocks, or (on a unified entry) a bare
    /// <c>text</c> field. Returns "" when none carry text.
    static string ContentText(JsonElement msg) {
        if (msg.Str("content") is { } s) return s;
        if (msg.Arr("content") is { } blocks) {
            var text = JoinTextBlocks(blocks, "text");
            if (text.Length > 0) return text;
            text = JoinTextBlocks(blocks, "output_text");
            if (text.Length > 0) return text;
            text = JoinTextBlocks(blocks, "input_text");
            if (text.Length > 0) return text;
        }
        return msg.Str("text") ?? "";
    }

    /// Vibe groups built-in tools under a namespace (<c>file_system.bash</c>); the bare name is what
    /// classifies and reads. An MCP tool with no prefix passes through unchanged.
    static string ToolName(string raw) {
        var dot = raw.LastIndexOf('.');
        return dot >= 0 && dot < raw.Length - 1 ? raw[(dot + 1)..] : raw;
    }

    // `arguments` is a JSON string; an object parses as the struct, anything else is wrapped.
    static Struct ArgumentsStruct(string? arguments) {
        if (arguments is not null) {
            try {
                using var parsed = JsonDocument.Parse(arguments);
                if (parsed.RootElement.IsObject) return StructOf(parsed.RootElement);
            } catch (JsonException) { }
        }
        return Wrap("arguments", arguments ?? "");
    }

    /// A unified-store entry's <c>createdAt</c> is epoch milliseconds (a number); a legacy
    /// messages.jsonl line has no timestamp, so the batch's receive time rides along.
    static (DateTimeOffset At, string? Record) ResolveTimestamp(JsonElement root, DateTimeOffset receivedAt) {
        if (root.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var ms)) {
            var at = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
            return (at, at.ToString("O"));
        }
        return TranscriptTime.Resolve(root.Str("createdAt") ?? root.Str("timestamp"), receivedAt);
    }

    static void Add(List<CanonicalEvent> events, IMessage payload, Guid id, DateTimeOffset at, string? recordTs) =>
        events.Add(new CanonicalEvent(CanonicalEventTypes.Of(payload), payload, id, at, recordTs));

    static ProjectionResult One(IMessage payload, Guid id, DateTimeOffset at, string? recordTs) =>
        ProjectionResult.Of([new CanonicalEvent(CanonicalEventTypes.Of(payload), payload, id, at, recordTs)]);
}

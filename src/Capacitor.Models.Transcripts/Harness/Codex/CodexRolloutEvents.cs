using System.Text;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// Codex's rollout (`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl`): an envelope per line with
/// `type` and `payload`. `response_item` is the conversation; `event_msg` is telemetry that lands
/// on events already returned, as an amendment or a UsageApplied, never by mutating them.
public sealed class CodexRolloutEvents : ITranscriptProjection {
    public static readonly CodexRolloutEvents Instance = new();

    CodexRolloutEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new CodexRolloutContext();

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");
            if (root.Str("type") is not { } type) return ProjectionResult.Empty;

            var ctx       = (CodexRolloutContext)context;
            var (at, raw) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);
            var rec       = new Record(TranscriptIds.CodexRecord(line), at, raw);
            var payload   = root.Obj("payload");

            return type switch {
                "session_meta"  => payload is { } p ? SessionMeta(p, rec) : ProjectionResult.Empty,
                "turn_context"  => TurnContext(payload, ctx),
                "response_item" => payload is { } p ? ResponseItem(p, rec, ctx) : ProjectionResult.Empty,
                "event_msg"     => payload is { } p ? EventMsg(p, rec, ctx) : ProjectionResult.Empty,
                "compacted"     => Compacted(payload, rec),
                _               => ProjectionResult.Empty,
            };
        }
    }

    readonly record struct Record(Guid Id, DateTimeOffset At, string? Raw) {
        public Timestamp Ts => Timestamp.FromDateTimeOffset(At);

        public CanonicalEvent Event(object payload) => new(CanonicalEventTypes.Of(payload), payload, Id, At, Raw);
    }

    // A collab subagent's rollout streams into its parent's session, where the subagent hooks carry
    // its lifecycle; its session_meta also names the parent's session id, not its own.
    static ProjectionResult SessionMeta(JsonElement payload, Record rec) {
        if (payload.Str("thread_source") == "subagent" || payload.Obj("source")?.Obj("subagent") is not null) return ProjectionResult.Empty;

        var ext = new CodexSessionExtension {
            Cwd           = payload.Str("cwd"),
            Originator    = payload.Str("originator"),
            CliVersion    = payload.Str("cli_version"),
            Source        = payload.Str("source"),
            ModelProvider = payload.Str("model_provider"),
            Git           = payload.Obj("git") is { } git
                ? new CodexGit { CommitHash = git.Str("commit_hash"), Branch = git.Str("branch"), RepositoryUrl = git.Str("repository_url") }
                : null,
        };
        var evt = new SessionStarted { Timestamp = rec.Ts };
        evt.Extensions[CodexExtension.Slug] = CodexExtension.Pack(ext, CodexJsonContext.Default.CodexSessionExtension);
        return ProjectionResult.Of([rec.Event(evt)]);
    }

    // A turn that omits its model clears the previous one rather than inheriting it.
    static ProjectionResult TurnContext(JsonElement? payload, CodexRolloutContext ctx) {
        if (payload is { } p) ctx.Model = p.Str("model") is { Length: > 0 } model ? model : null;
        return ProjectionResult.Empty;
    }

    static ProjectionResult ResponseItem(JsonElement payload, Record rec, CodexRolloutContext ctx) => payload.Str("type") switch {
        "message"           => Message(payload, rec, ctx),
        "agent_message"     => AgentMessage(payload, rec, ctx),
        "reasoning"         => Reasoning(payload, rec, ctx),
        "function_call" or "custom_tool_call"
                            => ToolCall(payload, rec, ctx),
        "web_search_call"   => WebSearch(payload, rec, ctx),
        "tool_search_call"  => ToolSearch(payload, rec, ctx),
        "function_call_output" or "custom_tool_call_output" or "tool_search_output"
                            => Output(payload, rec, ctx),
        _                   => ProjectionResult.Empty,
    };

    static ProjectionResult Member(CanonicalEvent evt, CodexRolloutContext ctx) {
        ctx.Track(evt);
        return ProjectionResult.Of([evt]);
    }

    // A user message that is only Codex's injected `<environment_context>` is kept: the chat hides it,
    // except when it is the receipt of a prompt the user typed in that shape.
    static ProjectionResult Message(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        if (payload.Arr("content") is not { } blocks) return ProjectionResult.Empty;
        var text = MessageText(blocks);
        if (text.Length == 0) return ProjectionResult.Empty;

        switch (payload.Str("role")) {
            case "user":
                return ProjectionResult.Of([rec.Event(new UserMessageReceived { Content = text, Timestamp = rec.Ts })]);
            case "assistant": {
                var evt = new AssistantTextGenerated { Content = text, Timestamp = rec.Ts };
                if (payload.Str("phase") is { } phase)
                    evt.Extensions[CodexExtension.Slug] = CodexExtension.Pack(new CodexAssistantTextExtension { Phase = phase }, CodexJsonContext.Default.CodexAssistantTextExtension);
                return Member(rec.Event(evt), ctx);
            }
            default:
                return ProjectionResult.Empty;
        }
    }

    static string MessageText(JsonElement blocks) {
        var sb = new StringBuilder();
        foreach (var block in blocks.EnumerateArray()) {
            if (block.Str("type") is not ("input_text" or "output_text" or "text") || block.Str("text") is not { } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    // The prose one agent sends another: model output, with its routing kept for the reader.
    static ProjectionResult AgentMessage(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        var text = payload.Arr("content") is { } blocks ? MessageText(blocks) : "";
        if (text.Length == 0) return ProjectionResult.Empty;

        var evt = new AssistantTextGenerated { Content = text, Timestamp = rec.Ts };
        evt.Extensions[CodexExtension.Slug] = CodexExtension.Nest(CodexExtension.AgentMessage,
            new CodexAgentMessageExtension { Author = payload.Str("author"), Recipient = payload.Str("recipient") },
            CodexJsonContext.Default.CodexAgentMessageExtension);
        return Member(rec.Event(evt), ctx);
    }

    // The flag reads "nothing to show": a summary beside an encrypted blob is readable reasoning.
    static ProjectionResult Reasoning(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        var encrypted = payload.Str("encrypted_content");
        var plaintext = payload.Str("content") ?? SummaryText(payload);

        var evt = new AssistantThinkingGenerated { Encrypted = plaintext.Length == 0 && encrypted is not null, Timestamp = rec.Ts };
        if (plaintext.Length > 0) evt.Content = plaintext;
        if (encrypted is not null) evt.Extensions[OpenAiExtension.Slug] = OpenAiExtension.Thinking(encrypted);
        return Member(rec.Event(evt), ctx);
    }

    static string SummaryText(JsonElement payload) {
        if (payload.Arr("summary") is not { } blocks) return "";
        var sb = new StringBuilder();
        foreach (var block in blocks.EnumerateArray()) {
            if (block.Str("text") is not { } text) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    // MCP tools split name and namespace; the canonical name joins them as `{namespace}{name}`, the
    // shape Claude's `mcp__server__tool` names already have.
    static ProjectionResult ToolCall(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        if (payload.Str("call_id") is not { } callId || payload.Str("name") is not { } name) return ProjectionResult.Empty;

        var ns       = payload.Str("namespace");
        var toolName = ns is { Length: > 0 } ? ns + name : name;
        var rawArgs  = payload.Str("arguments");
        var rawInput = payload.Str("input");

        var info = new ToolCallInfo { CallId = callId, ToolName = toolName, ToolKind = CodexToolKinds.Of(toolName, rawArgs ?? rawInput) };
        if (Arguments(rawArgs, rawInput) is { } args) info.Arguments = args;

        var evt = new AssistantToolCallsGenerated { Timestamp = rec.Ts };
        evt.ToolCalls.Add(info);
        if (ns is not null)
            evt.Extensions[CodexExtension.Slug] = CodexExtension.Pack(new CodexToolCallExtension { Name = name, Namespace = ns }, CodexJsonContext.Default.CodexToolCallExtension);
        return Member(rec.Event(evt), ctx);
    }

    // `arguments` is a JSON string; a custom tool's free-form `input` is kept under `input`. An
    // `arguments` string that is not an object is kept under `arguments` so the chat can show it.
    static Struct? Arguments(string? rawArgs, string? rawInput) {
        if (rawArgs is { Length: > 0 }) {
            try {
                using var doc = JsonDocument.Parse(rawArgs);
                if (doc.RootElement.IsObject) return StructOf(doc.RootElement);
            } catch (JsonException) { }
        }
        if (rawInput is not null) return Wrap("input", rawInput);
        return rawArgs is not null ? Wrap("arguments", rawArgs) : null;
    }

    /// A web search's record carries no call id; the `web_search_end` before it does, and is queued
    /// for it. Codex writes no output for a search, so the call is returned with a synthesized
    /// result, the call first so a reader pairing results to seen calls finds it.
    static ProjectionResult WebSearch(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        var callId = payload.Str("call_id");
        if (callId is null) {
            if (ctx.AssignedWebSearchCallIds.TryGetValue(rec.Id, out var assigned)) {
                callId = assigned;
            } else if (ctx.PendingWebSearchCallIds.TryDequeue(out var queued)) {
                callId = queued;
                ctx.AssignedWebSearchCallIds[rec.Id] = queued;
            }
        }
        callId ??= rec.Id.ToString("N");

        var call = new AssistantToolCallsGenerated { Timestamp = rec.Ts };
        call.ToolCalls.Add(new ToolCallInfo {
            CallId    = callId,
            ToolName  = "web_search",
            Arguments = payload.Obj("action") is { } action ? StructOf(action) : new Struct(),
            ToolKind  = CodexToolKinds.Of("web_search", null),
        });
        var callEvent = rec.Event(call);
        ctx.Track(callEvent);

        var resultAt = rec.At.AddTicks(1);
        var result   = new ToolResultReceived { CallId = callId, Result = payload.Str("status") ?? "", Timestamp = Timestamp.FromDateTimeOffset(resultAt) };
        return ProjectionResult.Of([
            callEvent,
            new CanonicalEvent(CanonicalEventTypes.ToolResultReceived, result, TranscriptIds.Sibling(rec.Id, "result"), resultAt, rec.Raw),
        ]);
    }

    /// A tool-registry lookup pairs with its own `tool_search_output`; only the name is ours to
    /// supply.
    static ProjectionResult ToolSearch(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        var call = new AssistantToolCallsGenerated { Timestamp = rec.Ts };
        call.ToolCalls.Add(new ToolCallInfo {
            CallId    = payload.Str("call_id") ?? rec.Id.ToString("N"),
            ToolName  = "tool_search",
            Arguments = payload.Obj("arguments") is { } args ? StructOf(args) : new Struct(),
            ToolKind  = CodexToolKinds.Of("tool_search", null),
        });
        return Member(rec.Event(call), ctx);
    }

    static ProjectionResult Output(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        if (payload.Str("call_id") is not { } callId) return ProjectionResult.Empty;

        var evt = new ToolResultReceived { CallId = callId, Timestamp = rec.Ts };
        if (OutputText(payload) is { } text) evt.Result = text;
        if (ctx.PendingExec.Remove(callId, out var exec))
            CodexExtension.Merge(evt.Extensions, CodexExtension.Nest(CodexExtension.Exec, exec, CodexJsonContext.Default.CodexExecResultExtension));
        if (ctx.PendingPatch.Remove(callId, out var patch))
            CodexExtension.Merge(evt.Extensions, CodexExtension.Nest(CodexExtension.Patch, patch, CodexJsonContext.Default.CodexPatchResultExtension));

        ctx.EmittedResults[callId] = rec.Id;
        return ProjectionResult.Of([rec.Event(evt)]);
    }

    static string? OutputText(JsonElement payload) =>
        payload.Str("output")
        ?? (payload.Arr("output") is { } blocks ? JoinTextBlocks(blocks, "input_text") : null)
        ?? payload.Arr("tools")?.GetRawText();

    static ProjectionResult EventMsg(JsonElement payload, Record rec, CodexRolloutContext ctx) => payload.Str("type") switch {
        "token_count"      => TokenCount(payload, rec, ctx),
        "exec_command_end" => ExecEnd(payload, ctx),
        "patch_apply_end"  => PatchEnd(payload, ctx),
        "task_complete"    => TaskComplete(payload, ctx),
        "web_search_end"   => WebSearchEnd(payload, ctx),
        _                  => ProjectionResult.Empty,
    };

    static ProjectionResult WebSearchEnd(JsonElement payload, CodexRolloutContext ctx) {
        if (payload.Str("call_id") is { } callId && ctx.EnqueuedWebSearchCallIds.Add(callId))
            ctx.PendingWebSearchCallIds.Enqueue(callId);
        return ProjectionResult.Empty;
    }

    /// One response's usage, for every member of its cluster. The canonical member is the first
    /// that is not thinking, which the chat drops; the rest carry echoes a sum must skip. Only a
    /// produced UsageApplied finalizes the cluster, so a repeated token_count, which can carry zeros
    /// after a compaction, never replaces the first.
    static ProjectionResult TokenCount(JsonElement payload, Record rec, CodexRolloutContext ctx) {
        if (ctx.Cluster.Count == 0 || ctx.ClusterFinalized) return ProjectionResult.Empty;
        if (payload.Obj("info") is not { } info || info.Obj("last_token_usage") is not { } last) return ProjectionResult.Empty;

        var usage = Usage(last, info.Num("model_context_window"), ctx.Model);

        var canonical = ctx.Cluster.FindIndex(m => m.EventType != CanonicalEventTypes.AssistantThinkingGenerated);
        if (canonical < 0) canonical = 0;

        var targets = new UsageTarget[ctx.Cluster.Count];
        for (var i = 0; i < targets.Length; i++) {
            var member = ctx.Cluster[i];
            targets[i] = new UsageTarget(member.EventId, member.EventType, member.ToolName, IsEcho: i != canonical);
        }

        ctx.ClusterFinalized = true;
        var anchor = ctx.Cluster[0].EventId;
        var applied = new UsageApplied(usage, anchor, targets);
        return ProjectionResult.Of([
            new CanonicalEvent(CanonicalEventTypes.UsageApplied, applied, TranscriptIds.Sibling(anchor, "usage-backfill"), rec.At, rec.Raw),
        ]);
    }

    // Codex counts cached input inside input; every reader adds the two, so input is stored net.
    // A block with none of the four counts yields an empty usage, which stamps nothing.
    static TokenUsage Usage(JsonElement last, long? window, string? model) {
        var gross     = last.Num("input_tokens");
        var cached    = last.Num("cached_input_tokens");
        var output    = last.Num("output_tokens");
        var reasoning = last.Num("reasoning_output_tokens");
        if (gross is null && cached is null && output is null && reasoning is null) return new TokenUsage();

        long? net = gross is { } g ? Math.Max(0, g - (cached ?? 0)) : null;
        var usage = new TokenUsage { TotalTokens = (net ?? 0) + (output ?? 0) + (cached ?? 0) };
        if (net is { } n) usage.InputTokens              = n;
        if (output is { } o) usage.OutputTokens          = o;
        if (cached is { } c) usage.CachedInputTokens     = c;
        if (reasoning is { } r) usage.ReasoningTokens    = r;
        if (model is not null) usage.Model               = model;
        if (window is { } w) {
            usage.AdditionalCounts = new Struct();
            usage.AdditionalCounts.Fields["model_context_window"] = Value.ForNumber(w);
        }
        return usage;
    }

    // Telemetry for a result returned earlier in this batch is an amendment to it; otherwise it
    // waits for the output still to come. Exec and patch share the index, and the first to amend a
    // result takes it out.
    static ProjectionResult ExecEnd(JsonElement payload, CodexRolloutContext ctx) {
        if (payload.Str("call_id") is not { } callId) return ProjectionResult.Empty;

        var exec = new CodexExecResultExtension {
            ExitCode   = (int?)payload.Num("exit_code"),
            DurationMs = DurationMs(payload.Obj("duration")),
            Stdout     = NullIfEmpty(payload.Str("stdout")),
            Stderr     = NullIfEmpty(payload.Str("stderr")),
            ParsedCmd  = payload.Prop("parsed_cmd")?.Clone(),
            Source     = payload.Str("source"),
        };
        if (ctx.EmittedResults.Remove(callId, out var target))
            return Amend(target, CodexExtension.Nest(CodexExtension.Exec, exec, CodexJsonContext.Default.CodexExecResultExtension));

        ctx.PendingExec[callId] = exec;
        return ProjectionResult.Empty;
    }

    // Written even when nothing changed, so a reader knows the call was answered and does not fall
    // back to the requested patch.
    static ProjectionResult PatchEnd(JsonElement payload, CodexRolloutContext ctx) {
        if (payload.Str("call_id") is not { } callId) return ProjectionResult.Empty;

        var changes = new List<CodexPatchChange>();
        if (payload.Obj("changes") is { } map) {
            foreach (var entry in map.EnumerateObject()) {
                changes.Add(new CodexPatchChange {
                    FilePath    = entry.Name,
                    Kind        = entry.Value.Str("type"),
                    UnifiedDiff = entry.Value.Str("unified_diff"),
                    Content     = entry.Value.Str("content"),
                    MovePath    = entry.Value.Str("move_path"),
                });
            }
        }
        var patch = new CodexPatchResultExtension { Changes = changes, Success = payload.Bool("success") };
        if (ctx.EmittedResults.Remove(callId, out var target))
            return Amend(target, CodexExtension.Nest(CodexExtension.Patch, patch, CodexJsonContext.Default.CodexPatchResultExtension));

        ctx.PendingPatch[callId] = patch;
        return ProjectionResult.Empty;
    }

    // The turn's timing lands on its cluster's first event; a caller that no longer holds it drops
    // the amendment.
    static ProjectionResult TaskComplete(JsonElement payload, CodexRolloutContext ctx) {
        if (ctx.Cluster.Count == 0) return ProjectionResult.Empty;
        var timing = new CodexTurnTimingExtension { DurationMs = payload.Num("duration_ms"), TimeToFirstTokenMs = payload.Num("time_to_first_token_ms") };
        return Amend(ctx.Cluster[0].EventId, CodexExtension.Nest(CodexExtension.Task, timing, CodexJsonContext.Default.CodexTurnTimingExtension));
    }

    static ProjectionResult Amend(Guid target, Struct block) => ProjectionResult.Of(new EventAmendment(target, CodexExtension.Slug, block));

    static double? DurationMs(JsonElement? duration) {
        if (duration is not { } d) return null;
        var ms = (d.Num("secs") ?? 0) * 1000.0 + (d.Num("nanos") ?? 0) / 1_000_000.0;
        return ms > 0 ? ms : null;
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // Codex keeps its encrypted summary as the `compaction` entry of the replacement history.
    static ProjectionResult Compacted(JsonElement? payload, Record rec) {
        var     history   = payload?.Prop("replacement_history");
        string? encrypted = null;
        if (history is { } entries && entries.IsArray) {
            foreach (var entry in entries.EnumerateArray()) {
                if (entry.Str("type") == "compaction" && entry.Str("encrypted_content") is { } enc) {
                    encrypted = enc;
                    break;
                }
            }
        }
        return ProjectionResult.Of([rec.Event(CompactionHistoryBound.Build(history, encrypted))]);
    }
}

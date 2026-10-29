using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema;
using Kurrent.Agent.Schema.Events;
using static Capacitor.Models.Transcripts.TranscriptText;

namespace Capacitor.Models.Transcripts.Harness.Claude;

public sealed class ClaudeTranscriptContext(string idScope) : TranscriptContext {
    /// "{session}:{agent}" for a subagent stream, the bare session id otherwise; attachment ids
    /// hash it.
    public string IdScope { get; } = idScope;

    /// The whole usage object of the last assistant record that carried one: Claude Code writes each block of
    /// one response as its own record with the response's usage repeated, and only the first counts.
    internal JsonElement? PreviousUsage { get; set; }
}

/// Claude Code's project transcript (`~/.claude/projects/&lt;slug&gt;/&lt;session&gt;.jsonl`): one JSON
/// record per line, `type` at the root, the API message under `message`.
public sealed class ClaudeTranscriptEvents : ITranscriptProjection {
    public static readonly ClaudeTranscriptEvents Instance = new();

    ClaudeTranscriptEvents() { }

    public TranscriptContext CreateContext(string sessionId, string? agentId) =>
        new ClaudeTranscriptContext(agentId is null ? sessionId : $"{sessionId}:{agentId}");

    public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        if (string.IsNullOrWhiteSpace(line)) return ProjectionResult.Reject("empty line");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); } catch (JsonException ex) { return ProjectionResult.Reject($"not JSON: {ex.Message}"); }

        using (doc) {
            var root = doc.RootElement;
            if (!root.IsObject) return ProjectionResult.Reject("not a JSON object");

            // Only a record the projection reads needs a usable id; every other type is ignored
            // whatever its uuid holds. A prompt or notification that lands mid-turn is one such
            // record: an `attachment` rather than the `user` line it would otherwise be — and the
            // attachments that are anything else are settled here, before the id is read.
            var ctx  = (ClaudeTranscriptContext)context;
            var type = root.Str("type");
            (string Prompt, string Content)? command = type == "system" && root.Str("subtype") == "local_command" ? LocalCommand(root) : null;
            if (type is not ("user" or "assistant" or "attachment") && command is null) {
                ctx.PreviousUsage = null;
                return ProjectionResult.Empty;
            }

            var queued = type == "attachment" ? QueuedCommand(root) : null;
            if (type == "attachment" && queued is null) {
                ctx.PreviousUsage = null;
                return ProjectionResult.Empty;
            }

            Guid recordId;
            switch (root.Prop("uuid")) {
                case null:
                    recordId = TranscriptIds.ClaudeFallback(lineNumber, line);
                    break;
                case { } uuid when uuid.IsString && Guid.TryParse(uuid.GetString(), out var parsed):
                    recordId = parsed;
                    break;
                default:
                    return ProjectionResult.Reject("uuid is not a GUID");
            }

            // Every valid record but an assistant one re-arms the usage memory; a rejected one, above, touches nothing.
            if (type != "assistant") ctx.PreviousUsage = null;

            var (at, recordTimestamp) = TranscriptTime.Resolve(root.Str("timestamp"), receivedAt);
            var record = new Record(recordId, at, recordTimestamp, root.Str("parentUuid"), root.Bool("isSidechain") == true, ctx.IdScope);

            return ProjectionResult.Of(type switch {
                "user"      => ProjectUser(root, record),
                "assistant" => ProjectAssistant(root, record, ctx),
                "system"    => ProjectLocalCommand(command!.Value, root, record),
                _           => ProjectAttachment(queued!.Value, root, record),
            });
        }
    }

    readonly record struct Record(Guid Id, DateTimeOffset At, string? RecordTimestamp, string? CausedBy, bool IsSidechain, string IdScope) {
        public Timestamp ProtoTimestamp => Timestamp.FromDateTimeOffset(At);
    }

    /// Assigns ids in emission order: the record id to the first event, a block sibling id to each
    /// later one, keyed by the raw index of the block that produced it.
    sealed class Emitter(Record record) {
        readonly List<CanonicalEvent> _events = [];

        public IReadOnlyList<CanonicalEvent> Events => _events;

        /// The record's usage, for its first emitted event only.
        public (TokenUsage Usage, long? CacheCreation)? Usage { get; set; }

        public void Add(int blockIndex, IMessage payload, Struct? claudeCode, IReadOnlyList<TranscriptAttachment>? attachments = null) {
            if (claudeCode is not null) SchemaExtensions.Of(payload)![ClaudeCodeExtension.Slug] = claudeCode;
            var first = _events.Count == 0;
            var id    = first ? record.Id : TranscriptIds.ClaudeBlock(record.Id, blockIndex);
            var usage = first ? Usage : null;
            _events.Add(new CanonicalEvent(CanonicalEventTypes.Of(payload), payload, id, record.At, record.RecordTimestamp, record.CausedBy,
                Usage: usage?.Usage, CacheCreationTokens: usage?.CacheCreation, Attachments: attachments is { Count: > 0 } ? attachments : null));
        }
    }

    static IReadOnlyList<CanonicalEvent> ProjectUser(JsonElement root, Record record) {
        if (root.Obj("message") is not { } message) return [];
        var emitter    = new Emitter(record);
        var isMeta     = root.Bool("isMeta") == true;
        var originKind = root.Obj("origin")?.Str("kind");
        var isCompactSummary = root.Bool("isCompactSummary") == true;
        var origin     = OriginOf(root);

        if (message.Str("content") is { } text) {
            if (!IsDeferredToolsInjection(text))
                emitter.Add(0, UserMessage(text, record), ClaudeCodeExtension.Flags(record.IsSidechain, isMeta, originKind, isCompactSummary: isCompactSummary, origin: origin));
            return emitter.Events;
        }
        if (message.Arr("content") is not { } blocks) return [];

        var index = 0;
        var sawResult = false;
        // The root object describes one result and names none: on a line with several it is attributed
        // to none of them, where the server's single event would carry it on the first.
        var toolUseResult = CountResults(blocks) == 1 ? root.Obj("toolUseResult") : null;
        foreach (var block in blocks.EnumerateArray()) {
            if (block.Str("type") == "tool_result") {
                var (result, raw) = ToolResult(block, record);
                emitter.Add(index, result, ClaudeCodeExtension.Flags(
                    record.IsSidechain, isError: block.Bool("is_error") == true,
                    toolUseResult: toolUseResult is { } obj ? StructOf(obj) : null, outputRaw: raw));
                sawResult = true;
            }
            index++;
        }
        // Text and image blocks beside tool results are dropped: the results are the record.
        if (sawResult) return emitter.Events;

        var (texts, attachments) = Content(blocks, record);
        if (texts.Count == 0 && attachments.Count == 0) return [];

        var joined = string.Join("\n", texts);
        if (IsDeferredToolsInjection(joined)) return [];
        emitter.Add(0, UserMessage(joined, record), ClaudeCodeExtension.Flags(record.IsSidechain, isMeta, originKind, isCompactSummary: isCompactSummary, origin: origin),
            attachments);
        return emitter.Events;
    }

    // The invocation and output are separate system records. Only commandRun on the result is a receipt.
    static (string Prompt, string Content)? LocalCommand(JsonElement root) {
        if (root.Bool("isMeta") == true || root.Bool("isSidechain") == true
            || root.Obj("origin")?.Str("kind") is not (null or "human")) return null;
        if (root.Obj("commandRun") is not { } run || run.Str("command") is not { Length: > 0 } name
            || name.StartsWith('/') || name.Any(char.IsWhiteSpace) || run.Str("args") is not { } args) return null;
        var prompt = "/" + name + (args.Trim().Length == 0 ? "" : " " + args.Trim());
        var content = "<command-name>/" + System.Net.WebUtility.HtmlEncode(name) + "</command-name><command-args>"
            + System.Net.WebUtility.HtmlEncode(args.Trim()) + "</command-args>";
        return (prompt, content);
    }

    static IReadOnlyList<CanonicalEvent> ProjectLocalCommand((string Prompt, string Content) command, JsonElement root, Record record) {
        var flags = ClaudeCodeExtension.Flags(record.IsSidechain, root.Bool("isMeta") == true, root.Obj("origin")?.Str("kind")) ?? new Struct();
        flags.Fields[ClaudeCodeExtension.LocalCommand] = Value.ForString(command.Prompt);
        var emitter = new Emitter(record);
        emitter.Add(0, UserMessage(command.Content, record), flags);
        return emitter.Events;
    }

    static int CountResults(JsonElement blocks) {
        var count = 0;
        foreach (var block in blocks.EnumerateArray()) if (block.Str("type") == "tool_result") count++;
        return count;
    }

    // The protobuf parser refuses some JSON System.Text.Json accepts (an out-of-range number, a lone
    // surrogate); the text still stands without its raw copy.
    static Value? RawValue(JsonElement element) {
        try {
            return Value.Parser.ParseJson(element.GetRawText());
        } catch (Exception ex) when (ex is InvalidJsonException or InvalidProtocolBufferException) {
            return null;
        }
    }

    static ClaudeCodeExtension.Origin OriginOf(JsonElement line) =>
        new(line.Obj("origin")?.Bool("handback") == true, line.Str("promptSource"),
            line.Obj("turnPosition")?.Num("promptIndex") is { } index and >= int.MinValue and <= int.MaxValue ? (int)index : null);

    /// A content array's text blocks, and its base64 image blocks as attachments. Ids count images, not
    /// blocks: the server's persisted blob streams are keyed by them.
    static (List<string> Texts, List<TranscriptAttachment> Attachments) Content(JsonElement blocks, Record record) {
        var texts       = new List<string>();
        var attachments = new List<TranscriptAttachment>();
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                // A text block counts even when empty or null, as the server counts it.
                case "text":
                    if (block.Prop("text") is not null) texts.Add(block.Str("text") ?? "");
                    break;
                case "image":
                    if (block.Obj("source") is { } source && source.Str("type") == "base64"
                     && source.Str("data") is { } data && source.Prop("media_type") is not null
                     && Decode(data) is { } bytes) {
                        var mediaType = source.Str("media_type") ?? "application/octet-stream";
                        var id = TranscriptIds.ClaudeAttachment(record.IdScope, record.Id, attachments.Count);
                        attachments.Add(new TranscriptAttachment(id, $"attachment-{id.ToString()[..8]}{Extension(mediaType)}", mediaType, bytes));
                    }
                    break;
            }
        }
        return (texts, attachments);
    }

    static byte[]? Decode(string base64) {
        try { return Convert.FromBase64String(base64); } catch (FormatException) { return null; }
    }

    static string Extension(string mediaType) => mediaType switch {
        "image/png"     => ".png",
        "image/jpeg"    => ".jpeg",
        "image/jpg"     => ".jpg",
        "image/gif"     => ".gif",
        "image/webp"    => ".webp",
        "image/svg+xml" => ".svg",
        _               => ".bin",
    };

    static UserMessageReceived UserMessage(string text, Record record) =>
        new() { Content = text, Timestamp = record.ProtoTimestamp };

    /// What a <c>queued_command</c> attachment carries: how Claude Code delivers a task notification
    /// or a typed prompt that lands while the parent is mid-turn. For such a prompt the attachment is
    /// its only record. Null for anything else an attachment holds — a snapshot, a file.
    static (JsonElement Prompt, string Mode, string? OriginKind)? QueuedCommand(JsonElement root) {
        if (root.Obj("attachment") is not { } attachment || attachment.Str("type") != "queued_command") return null;
        switch (attachment.Str("commandMode")) {
            case "task-notification":
                return attachment.Prop("prompt") is { } notification && (notification.IsString || notification.IsArray)
                    ? (notification, "task-notification", attachment.Obj("origin")?.Str("kind") ?? "task-notification") : null;
            case "prompt":
                return attachment.Prop("prompt") is { } prompt && (prompt.IsString || prompt.IsArray)
                    ? (prompt, "prompt", attachment.Obj("origin")?.Str("kind")) : null;
            default:
                return null;
        }
    }

    static IReadOnlyList<CanonicalEvent> ProjectAttachment((JsonElement Prompt, string Mode, string? OriginKind) queued, JsonElement root, Record record) {
        string text;
        List<TranscriptAttachment> attachments;
        if (queued.Prompt.IsString) {
            text        = queued.Prompt.GetString() ?? "";
            attachments = [];
            if (text.Length == 0) return [];
        } else {
            var content = Content(queued.Prompt, record);
            if (content.Texts.Count == 0 && content.Attachments.Count == 0) return [];
            text        = string.Join("\n", content.Texts);
            attachments = content.Attachments;
        }

        // Who wrote a queued command is recorded on the attachment itself, as the server reads it.
        var attachment = root.Obj("attachment")!.Value;
        var origin     = OriginOf(attachment) with { IsQueued = true, CommandMode = queued.Mode };
        var emitter    = new Emitter(record);
        emitter.Add(0, UserMessage(text, record),
            ClaudeCodeExtension.Flags(record.IsSidechain, root.Bool("isMeta") == true || attachment.Bool("isMeta") == true, queued.OriginKind,
                isCompactSummary: attachment.Bool("isCompactSummary") == true, origin: origin),
            attachments);
        return emitter.Events;
    }

    static (ToolResultReceived Result, Value? Raw) ToolResult(JsonElement block, Record record) {
        var evt = new ToolResultReceived { CallId = block.Str("tool_use_id") ?? "", Timestamp = record.ProtoTimestamp };
        var (text, raw) = ResultText(block.Prop("content"));
        if (text is not null) evt.Result = text;
        return (evt, raw);
    }

    // The server's rule: a string as is; an array by its text blocks joined with a blank line, or verbatim
    // when it has none; anything else verbatim; nothing for an absent or null one. Whatever was not a
    // string is also kept whole as the raw output.
    static (string? Text, Value? Raw) ResultText(JsonElement? content) {
        if (content is not { } c || c.IsNull) return (null, null);
        if (c.IsString) return (c.GetString(), null);

        var raw = RawValue(c);
        if (!c.IsArray) return (c.GetRawText(), raw);

        var texts = new List<string>();
        foreach (var block in c.EnumerateArray())
            if (block.Str("type") == "text" && block.Str("text") is { } t) texts.Add(t);
        return (texts.Count > 0 ? string.Join("\n\n", texts) : c.GetRawText(), raw);
    }

    static bool IsDeferredToolsInjection(string text) => text.AsSpan().TrimStart().StartsWith("<available-deferred-tools");

    static IReadOnlyList<CanonicalEvent> ProjectAssistant(JsonElement root, Record record, ClaudeTranscriptContext ctx) {
        if (root.Obj("message") is not { } message || message.Arr("content") is not { } blocks || blocks.GetArrayLength() == 0) return [];
        var emitter = new Emitter(record) { Usage = UsageOf(message, ctx) };
        var index   = 0;
        // Flags are built per block: two messages must never share one Struct instance.
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "text":
                    if (block.Str("text") is { Length: > 0 } text)
                        emitter.Add(index, new AssistantTextGenerated { Content = text, Timestamp = record.ProtoTimestamp }, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                case "thinking": {
                    var thinking = new AssistantThinkingGenerated { Content = block.Str("thinking") ?? "", Encrypted = false, Timestamp = record.ProtoTimestamp };
                    if (block.Str("signature") is { } signature) thinking.Signature = signature;
                    emitter.Add(index, thinking, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                }
                case "tool_use": {
                    var call = new AssistantToolCallsGenerated { Timestamp = record.ProtoTimestamp };
                    call.ToolCalls.Add(new ToolCallInfo {
                        CallId    = block.Str("id") ?? "",
                        ToolName  = block.Str("name") ?? "",
                        Arguments = ToolInput(block),
                        ToolKind  = ClaudeToolKinds.Of(block.Str("name")),
                    });
                    emitter.Add(index, call, ClaudeCodeExtension.Flags(record.IsSidechain));
                    break;
                }
            }
            index++;
        }
        return emitter.Events;
    }

    /// The record's usage when it is new: compared against the memory and then stored, whether or not the
    /// record goes on to emit anything. A record without usage leaves the memory as it was.
    static (TokenUsage, long?)? UsageOf(JsonElement message, ClaudeTranscriptContext ctx) {
        if (message.Obj("usage") is not { } usage) return null;

        var input      = usage.Num("input_tokens");
        var output     = usage.Num("output_tokens");
        var cacheRead  = usage.Num("cache_read_input_tokens");
        var cacheWrite = usage.Num("cache_creation_input_tokens");
        var reasoning  = usage.Num("reasoning_tokens");
        var total      = usage.Num("total_tokens");
        // The whole object, so a field outside the canonical counts still tells two responses apart.
        var repeat     = ctx.PreviousUsage is { } previous && JsonElement.DeepEquals(previous, usage);
        ctx.PreviousUsage = usage.Clone();
        var extras = StructOf(usage);
        foreach (var key in CanonicalUsageKeys) extras.Fields.Remove(key);
        var additive = input is not null || output is not null || cacheRead is not null || cacheWrite is not null;
        if (repeat || !additive && reasoning is null && total is null && extras.Fields.Count == 0) return null;

        // A vendor's own total wins: the agent schema maps it to the canonical slot as reported. The computed total is
        // the four additive counts, so a usage with none of them gets no total rather than a reported zero.
        var token = new TokenUsage();
        if ((total ?? (additive ? (input ?? 0) + (output ?? 0) + (cacheRead ?? 0) + (cacheWrite ?? 0) : null)) is { } sum)
            token.TotalTokens = sum;
        if (input is { } i) token.InputTokens            = i;
        if (output is { } o) token.OutputTokens          = o;
        if (cacheRead is { } r) token.CachedInputTokens  = r;
        if (reasoning is { } t) token.ReasoningTokens    = t;
        if (message.Str("model") is { } model) token.Model = model;
        if (extras.Fields.Count > 0) token.AdditionalCounts = extras;
        return (token, cacheWrite is > 0 ? cacheWrite : null);
    }

    /// Every other usage key rides `additional_counts` verbatim, as the agent schema's producers write it:
    /// the cache-write split, server-tool requests and service tier are what a cost calculation prices.
    static readonly string[] CanonicalUsageKeys =
        ["input_tokens", "output_tokens", "total_tokens", "cache_read_input_tokens", "reasoning_tokens"];

    // Arguments is always an object: a non-object input is wrapped, an absent one is empty.
    static Struct ToolInput(JsonElement block) =>
        block.Obj("input") is { } obj ? StructOf(obj)
        : block.Prop("input") is { } value ? Wrap("input", value)
        : new Struct();
}

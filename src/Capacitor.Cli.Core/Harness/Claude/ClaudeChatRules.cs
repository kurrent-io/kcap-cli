using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Cli.Core.Harness.Claude;

/// What the chat hides or rewrites in Claude records: meta and sidechain records, the blocks
/// Claude Code injects around a user turn, and the finished-background-task record it injects as
/// if the user had spoken. Also where a subagent's or a background command's launch, detachment
/// and end are read.
public sealed partial class ClaudeChatRules : IChatDisplayRules {
    public static readonly ClaudeChatRules Instance = new(ownTranscript: false);

    /// For a subagent's own transcript, every record of which is a sidechain record.
    public static readonly ClaudeChatRules Subagent = new(ownTranscript: true);

    readonly bool _ownTranscript;

    public bool StartsSubagentRows => true;

    const string StoppedTaskMessage = "Successfully stopped task";
    const string HandbackPointer = "This agent's report was delivered to you as a message";

    ClaudeChatRules(bool ownTranscript) => _ownTranscript = ownTranscript;

    public IReadOnlyList<string> LiteralInputs(CanonicalEvent evt, AcpEventEnvelope raw) {
        if (raw.Kind != AcpEventKind.UserMessage || raw.Text is not { Length: > 0 } text) return [];
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Text(slug, ClaudeCodeExtension.LocalCommand) is not null
            || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)
            || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)
            || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsCompactSummary)
            || SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind) is not (null or "human")) return [];
        // Try the exact text first: a user can be documenting the paste wrapper itself. Unwrap
        // once, without stripping reminders/commands that may be part of that literal paste.
        var pasted = UnwrapPastes(text.Replace("\r\n", "\n"));
        return pasted == text ? [text] : [text, pasted];
    }

    public string? SubmittedInput(CanonicalEvent evt, AcpEventEnvelope raw, AcpEventEnvelope? displayed) {
        if (raw.Kind != AcpEventKind.UserMessage) return null;
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)
            || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)
            || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsCompactSummary)
            || IsTaskNotification(slug, raw)
            || PeerMessage(slug, raw.Text) is not null) return null;

        var text = raw.Text ?? "";
        if (SchemaExtensions.Text(slug, ClaudeCodeExtension.LocalCommand) is { } localCommand)
            return SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind) is null or "human" ? localCommand : null;
        if (BashCommand(text) is { } command) return "!" + command;
        var name = CommandName().Match(text).Groups[1].Value.Trim();
        if (name.StartsWith('/')) {
            var args = CommandArgs().Match(text).Groups[1].Value.Trim();
            return args.Length == 0 ? name : $"{name} {args}";
        }
        return displayed is { Kind: AcpEventKind.UserMessage } user ? user.Text : null;
    }

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) {
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Text(slug, ClaudeCodeExtension.LocalCommand) is not null) return null;
        if (!_ownTranscript && SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)) return null;
        // A peer message delivered while the session is idle is a meta record, and it is the
        // only record of that message.
        if (envelope.Kind == AcpEventKind.UserMessage && PeerMessage(slug, envelope.Text) is { } peer)
            return peer.Body.Length == 0 ? null : envelope with { Kind = AcpEventKind.SystemNote, Text = peer.Body, ToolKind = peer.Kind };
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)) return null;

        switch (envelope.Kind) {
            case AcpEventKind.UserMessage: {
                // Claude Code writes its compaction summary as a user record; the user never typed it.
                if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsCompactSummary))
                    return envelope.Text?.Trim() is { Length: > 0 } summary
                        ? envelope with { Kind = AcpEventKind.SystemNote, Text = summary, ToolKind = ChatDisplayKind.CompactSummary }
                        : null;
                if (IsTaskNotification(slug, envelope)) return TaskNotificationNote(envelope);
                var raw = envelope.Text ?? "";
                // A bang command is its own user record: the command in bash-input, the output in
                // bash-stdout/stderr. A message that only quotes those tags is left as written.
                if (BashCommand(raw) is { } command)
                    return envelope with { Text = "! " + WithoutAttachmentTrailer(command), ToolKind = ChatDisplayKind.Shell };
                if (BashOutput(raw) is { } output)
                    return output.Length == 0 ? null : envelope with { Kind = AcpEventKind.SystemNote, Text = output, ToolKind = ChatDisplayKind.Shell };
                var text = StripWrappers(raw);
                return text.Length == 0 ? null : envelope with { Text = text };
            }
            case AcpEventKind.ToolCall:
                return envelope with { ToolKind = ClaudeToolKinds.Of(envelope.ToolName) };
            case AcpEventKind.ToolResult:
                return envelope with {
                    ToolIsError = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsError),
                    ToolResult  = ChatResultText(slug) ?? envelope.ToolResult,
                };
            default:
                return envelope;
        }
    }

    /// The chat shows a block result's text blocks one per line, where the stored result joins them with
    /// a blank line, as the server writes it.
    static string? ChatResultText(Google.Protobuf.WellKnownTypes.Struct? slug) {
        if (slug is null || !slug.Fields.TryGetValue(ClaudeCodeExtension.OutputRaw, out var raw) || raw.ListValue is not { } blocks) return null;

        // The chat's own join: no separator ahead of the first text it has kept.
        var sb    = new System.Text.StringBuilder();
        var found = false;
        foreach (var v in blocks.Values) {
            if (v.StructValue is not { } b || SchemaExtensions.Text(b, "type") != "text" || SchemaExtensions.Text(b, "text") is not { } t) continue;
            found = true;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t);
        }
        return found ? TranscriptEnvelopes.Cap(sb.ToString()) : null;
    }

    /// A sidechain event is a subagent's own record: its runs are not the parent's rows, but are its own
    /// view's. The meta flag is not consulted: a hidden row still ends its subagent.
    public IReadOnlyList<RunSignal> Runs(CanonicalEvent evt, AcpEventEnvelope raw) {
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (!_ownTranscript && SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)) return [];

        switch (raw.Kind) {
            case AcpEventKind.ToolCall when raw.ToolName is "Agent" or "Task" && raw.ToolCallId is { Length: > 0 } callId: {
                var (name, description) = SpawnFacts(raw.ToolInputJson);
                return [new RunSignal.Started(callId, name, description, evt.Timestamp)];
            }
            case AcpEventKind.ToolCall when raw.ToolName is "Bash" && raw.ToolCallId is { Length: > 0 } callId: {
                var (name, detail) = ShellFacts(raw.ToolInputJson);
                return [new RunSignal.Started(callId, name, detail, evt.Timestamp, RunKind.Shell, Provisional: true)];
            }
            case AcpEventKind.ToolResult when raw.ToolCallId is { Length: > 0 } callId && ToolUseResult(slug) is { } result: {
                if (SchemaExtensions.Text(result, "backgroundTaskId") is { Length: > 0 } backgroundId)
                    return [new RunSignal.Detached(callId, backgroundId)];
                var resultAgentId = SchemaExtensions.Text(result, "agentId") ?? SchemaExtensions.Text(result, "agent_id");
                switch (SchemaExtensions.Text(result, "status")) {
                    case "async_launched" when resultAgentId is { Length: > 0 }:
                        return [new RunSignal.Detached(callId, resultAgentId)];
                    case "completed" when resultAgentId is { Length: > 0 }:
                        return [new RunSignal.Identified(callId, resultAgentId)];
                }
                // The message gate keeps a TaskGet or TaskOutput probe, which carries the same
                // task_id, from ending a running row.
                if (SchemaExtensions.Text(result, "task_type") is "local_agent" or "local_bash"
                    && SchemaExtensions.Text(result, "task_id") is { Length: > 0 } taskId
                    && (SchemaExtensions.Text(result, "message") ?? "").AsSpan().TrimStart().StartsWith(StoppedTaskMessage, StringComparison.OrdinalIgnoreCase))
                    return [new RunSignal.Finished(null, taskId, RunOutcome.Stopped, evt.Timestamp)];
                return [];
            }
            case AcpEventKind.UserMessage when IsTaskNotification(slug, raw): {
                var text = raw.Text ?? "";
                var callId = Tag(TaskToolUseId(), text);
                var agentId = Tag(TaskId(), text);
                if (callId is null && agentId is null) return [];
                // A cut-off notification has no status and still says the run ended; a status
                // that is not an end, such as running, says nothing.
                RunOutcome? outcome;
                switch (Tag(TaskStatus(), text)?.ToLowerInvariant()) {
                    case null:        outcome = null; break;
                    case "completed": outcome = RunOutcome.Done; break;
                    case "failed":    outcome = RunOutcome.Failed; break;
                    case "killed":    outcome = RunOutcome.Stopped; break;
                    case "stopped":   outcome = RunOutcome.Stopped; break;
                    default:          return [];
                }
                return [new RunSignal.Finished(callId, agentId, outcome, evt.Timestamp)];
            }
            default:
                return [];
        }
    }

    /// The server's events carry no origin_kind, so the text is the other way to know.
    static bool IsTaskNotification(Struct? slug, AcpEventEnvelope raw) =>
        SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind) == "task-notification"
        || (raw.Text ?? "").AsSpan().TrimStart().StartsWith("<task-notification>");

    const string PeerOpen = "<agent-message from=\"";
    const string PeerClose = "</agent-message>";
    const string PeerLead = "Another Claude session sent a message";
    const string HandbackMark = "[Subagent hand-back]";
    const string HandbackLead = "The report follows:\n";

    /// The body of a message another agent of this session sent, and which kind it is; null for
    /// anything else. The server's events carry no origin_kind, so without one the wrapper is
    /// matched instead: it opens the text, after the one lead line Claude Code puts in front of an
    /// idle delivery. Any other origin keeps a prompt that quotes the wrapper the user's own.
    static (string Body, string Kind)? PeerMessage(Struct? slug, string? text) {
        var origin = SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind);
        if (text is null || origin is not (null or "peer")) return null;
        var span = text.AsSpan().TrimStart();
        if (span.StartsWith(PeerLead, StringComparison.Ordinal) && span.IndexOf('\n') is var lead and >= 0)
            span = span[(lead + 1)..];
        if (!span.StartsWith(PeerOpen, StringComparison.Ordinal))
            return origin == "peer" ? (text.Trim(), ChatDisplayKind.AgentMessage) : null;
        var bodyStart = span.IndexOf('\n');
        if (bodyStart < 0) return origin == "peer" ? (text.Trim(), ChatDisplayKind.AgentMessage) : null;
        span = span[(bodyStart + 1)..];
        // Text after the close is Claude Code's guidance to the model. A stored record can end
        // before the close.
        var close = span.LastIndexOf(PeerClose, StringComparison.Ordinal);
        if (close >= 0) span = span[..close];
        if (!span.StartsWith(HandbackMark, StringComparison.Ordinal))
            return (span.Trim().ToString(), ChatDisplayKind.AgentMessage);
        var report = span.IndexOf(HandbackLead, StringComparison.Ordinal);
        return report < 0
            ? (span.Trim().ToString(), ChatDisplayKind.AgentReport)
            : (Outdent(span[(report + HandbackLead.Length)..]), ChatDisplayKind.AgentReport);
    }

    /// Claude Code indents every line of a hand-back by two spaces; left in, they would end up in
    /// every line of a code block in the report.
    static string Outdent(ReadOnlySpan<char> report) {
        var built = new StringBuilder(report.Length);
        foreach (var line in report.EnumerateLines()) {
            var cut = line.StartsWith("  ") ? line[2..] : line.TrimStart(' ');
            built.Append(cut).Append('\n');
        }
        return built.ToString().Trim();
    }

    static Struct? ToolUseResult(Struct? slug) =>
        slug is not null && slug.Fields.TryGetValue(ClaudeCodeExtension.ToolUseResult, out var v) && v.KindCase == Value.KindOneofCase.StructValue
            ? v.StructValue : null;

    static (string Name, string Description) SpawnFacts(string? inputJson) {
        if (inputJson is null) return ("agent", "");
        try {
            using var doc = JsonDocument.Parse(inputJson);
            var input = doc.RootElement;
            return (input.Str("subagent_type") is { Length: > 0 } type ? type : "agent", input.Str("description") ?? "");
        } catch (JsonException) {
            return ("agent", "");
        }
    }

    const int ShellNameLimit = 80;

    /// The row's name is the call's description, with the command's first line under it; a call
    /// without a description is named by that line, which goes under it only once the name is cut.
    static (string Name, string Detail) ShellFacts(string? inputJson) {
        if (inputJson is null) return ("command", "");
        try {
            using var doc = JsonDocument.Parse(inputJson);
            var input = doc.RootElement;
            var command = FirstLine(input.Str("command") ?? "");
            var described = input.Str("description") is { } d && d.Trim() is { Length: > 0 } trimmed ? trimmed : null;
            var name = described ?? (command.Length > 0 ? command : "command");
            var cut = name.Length > ShellNameLimit;
            return (cut ? name[..(ShellNameLimit - 1)] + "…" : name, described is null && !cut ? "" : command);
        } catch (JsonException) {
            return ("command", "");
        }
    }

    static string FirstLine(string text) {
        var trimmed = text.TrimStart();
        var end = trimmed.IndexOf('\n');
        return (end < 0 ? trimmed : trimmed[..end]).TrimEnd();
    }

    static string? Tag(Regex tag, string text) =>
        tag.Match(text) is { Success: true } m && m.Groups[1].Value.Trim() is { Length: > 0 } value ? value : null;

    // System-attributed: the summary in bold, then the result as markdown; a notification with
    // neither shows whatever is left once the wrapper tags are gone.
    static AcpEventEnvelope? TaskNotificationNote(AcpEventEnvelope envelope) {
        var raw     = envelope.Text ?? "";
        var summary = Tag(TaskSummary(), raw) ?? "";
        var body    = Tag(TaskResult(), raw) ?? "";
        // An agent that handed its report back says so in the result instead of repeating it.
        if (body.StartsWith(HandbackPointer, StringComparison.Ordinal)) body = "";
        var parts   = new List<string>(2);
        if (summary.Length > 0) parts.Add($"**{summary}**");
        if (body.Length > 0) parts.Add(body);
        var text = parts.Count > 0 ? string.Join("\n\n", parts) : TaskWrapper().Replace(raw, "").Trim();
        return text.Length == 0 ? null : envelope with { Kind = AcpEventKind.SystemNote, Text = text };
    }

    /// Removes the blocks Claude Code injects around a user turn: reminders and slash-command
    /// echoes are dropped, a paste keeps its text.
    internal static string StripWrappers(string text) => Wrappers().Replace(UnwrapPastes(text), "").Trim();

    const string PasteOpen = "<pasted_content id=\"";

    /// A paste is a block whose close repeats the id, with a newline right after the open tag and
    /// right before the close. Only those newlines come off: a one-line pair is prose, and spaces
    /// inside the block stay so the queue still matches.
    static string UnwrapPastes(string text) {
        var cursor = 0;
        var scan = 0;
        StringBuilder? built = null;
        while (scan < text.Length) {
            var open = text.IndexOf(PasteOpen, scan, StringComparison.Ordinal);
            if (open < 0) break;
            var idStart = open + PasteOpen.Length;
            var idEnd = text.IndexOf('"', idStart);
            if (idEnd < 0 || idEnd + 1 >= text.Length || text[idEnd + 1] != '>') {
                scan = open + PasteOpen.Length;
                continue;
            }
            var afterOpen = idEnd + 2;
            if (afterOpen >= text.Length || text[afterOpen] != '\n') {
                scan = afterOpen;
                continue;
            }
            var contentStart = afterOpen + 1;
            var close = string.Concat("\n</pasted_content id=\"", text.AsSpan(idStart, idEnd - idStart), "\">");
            var closeAt = text.IndexOf(close, contentStart, StringComparison.Ordinal);
            if (closeAt < 0) {
                scan = afterOpen;
                continue;
            }
            built ??= new StringBuilder(text.Length);
            built.Append(text, cursor, open - cursor);
            built.Append(text, contentStart, closeAt - contentStart);
            cursor = closeAt + close.Length;
            scan = cursor;
        }
        if (built is null) return text;
        built.Append(text, cursor, text.Length - cursor);
        return built.ToString();
    }

    /// The command when the whole message is a bash-input tag; null when the tag is quoted
    /// inside other text or the command is blank.
    static string? BashCommand(string text) {
        var tag = BashInput().Match(text);
        if (!tag.Success || BashInput().Replace(text, "").Trim().Length > 0) return null;
        var command = tag.Groups[1].Value.Trim();
        return command.Length == 0 ? null : command;
    }

    /// The queue matches the daemon's attachment trailer. The chat shows the command that was typed.
    static string WithoutAttachmentTrailer(string command) {
        var typed = AttachmentTrailer.Strip(command);
        return typed.Length == 0 ? command : typed;
    }

    /// Combined stdout and stderr when the whole message is those tags; empty when they are
    /// blank, null when other text remains. Close tags may be missing on a truncated record.
    static string? BashOutput(string text) {
        var stdout = BashStdout().Match(text);
        var stderr = BashStderr().Match(text);
        if (!stdout.Success && !stderr.Success) return null;
        if (BashStderr().Replace(BashStdout().Replace(text, ""), "").Trim().Length > 0) return null;
        var parts = new List<string>(2);
        if (stdout.Success && stdout.Groups[1].Value.Trim() is { Length: > 0 } o) parts.Add(o);
        if (stderr.Success && stderr.Groups[1].Value.Trim() is { Length: > 0 } e) parts.Add(e);
        return string.Join("\n", parts);
    }

    [GeneratedRegex(@"<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgs();

    // A stored notification can end before its closing tag, so it stays optional in the pattern.
    [GeneratedRegex(@"<summary>(.*?)(?:</summary>|$)", RegexOptions.Singleline)]
    private static partial Regex TaskSummary();

    [GeneratedRegex(@"<result>(.*?)(?:</result>|$)", RegexOptions.Singleline)]
    private static partial Regex TaskResult();

    [GeneratedRegex(@"<task-id>(.*?)(?:</task-id>|$)", RegexOptions.Singleline)]
    private static partial Regex TaskId();

    [GeneratedRegex(@"<tool-use-id>(.*?)(?:</tool-use-id>|$)", RegexOptions.Singleline)]
    private static partial Regex TaskToolUseId();

    [GeneratedRegex(@"<status>(.*?)(?:</status>|$)", RegexOptions.Singleline)]
    private static partial Regex TaskStatus();

    [GeneratedRegex(@"</?task-notification>")]
    private static partial Regex TaskWrapper();

    [GeneratedRegex(@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-caveat)>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex Wrappers();

    [GeneratedRegex(@"<bash-input>(.*?)</bash-input>", RegexOptions.Singleline)]
    private static partial Regex BashInput();

    // A stored record can end before its closing tag, so the close stays optional.
    [GeneratedRegex(@"<bash-stdout>(.*?)(?:</bash-stdout>|$)", RegexOptions.Singleline)]
    private static partial Regex BashStdout();

    [GeneratedRegex(@"<bash-stderr>(.*?)(?:</bash-stderr>|$)", RegexOptions.Singleline)]
    private static partial Regex BashStderr();
}

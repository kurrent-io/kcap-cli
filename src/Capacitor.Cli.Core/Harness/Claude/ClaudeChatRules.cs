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
    public static readonly ClaudeChatRules Instance = new();

    const string StoppedTaskMessage = "Successfully stopped task";

    ClaudeChatRules() { }

    public string? SubmittedInput(CanonicalEvent evt, AcpEventEnvelope raw, AcpEventEnvelope? displayed) {
        if (raw.Kind != AcpEventKind.UserMessage) return null;
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)
            || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)
            || IsTaskNotification(slug, raw)) return null;

        var text = raw.Text ?? "";
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
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain) || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)) return null;

        switch (envelope.Kind) {
            case AcpEventKind.UserMessage: {
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
                return envelope with { ToolIsError = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsError) };
            default:
                return envelope;
        }
    }

    /// A sidechain event is a subagent's own record, and a nested subagent is not the parent's
    /// row. The meta flag is not consulted: a hidden row still ends its subagent.
    public IReadOnlyList<RunSignal> Runs(CanonicalEvent evt, AcpEventEnvelope raw) {
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)) return [];

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
                if (SchemaExtensions.Text(result, "status") == "async_launched"
                    && (SchemaExtensions.Text(result, "agentId") ?? SchemaExtensions.Text(result, "agent_id")) is { Length: > 0 } agentId)
                    return [new RunSignal.Detached(callId, agentId)];
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
        var split = command.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (split <= 0) return command;
        if (!command.AsSpan(split + 2).StartsWith(AttachmentTrailer.Prefix, StringComparison.Ordinal)) return command;
        var typed = command[..split].TrimEnd();
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

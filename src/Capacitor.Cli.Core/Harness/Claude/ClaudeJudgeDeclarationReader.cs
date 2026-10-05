namespace Capacitor.Cli.Core.Harness.Claude;

using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Policy;
using Capacitor.Models.Transcripts;
using Capacitor.Models.Transcripts.Harness.Claude;
using Kurrent.Agent.Schema.Events;

/// <summary>
/// Reads the judge's declared evidence from a Claude transcript file: the last human user messages
/// (the turn set the server verifies against its own recording) and every call a human refused at a
/// prompt, session-wide, subagent transcripts included.
/// </summary>
/// <remarks>
/// A human message is what the transcript projection records as a user message, minus meta lines,
/// sidechain lines and harness payloads — the server's own definition, so a declared id it rejects
/// costs only the window. The refusal list's <c>complete</c> is false whenever anything about it is
/// unknown: the judge then refuses to allow, which costs a prompt; a false <c>true</c> would hide
/// a "no" the human already gave.
/// <para>With a state path, each read resumes where the last one stopped: a long session's
/// transcript is tens of megabytes, and a full scan per tool call would spend the judge's budget on
/// reading.</para>
/// </remarks>
public static class ClaudeJudgeDeclarationReader {
    public const int MaxTurns = 16;
    public const int MaxRefusals = 32;
    const int MaxReference = 128;
    const int MaxTool = 256;
    const int MaxTarget = 1024;
    const int MaxRetainedRefusals = 256;

    /// <summary>The most one read takes in, across every file: about 0.4 s of scanning, so a session
    /// whose backlog is larger catches up over several hooks instead of outliving one.</summary>
    internal const long MaxBytesPerRead = 16 * 1024 * 1024;

    // Claude Code writes a declined prompt as an error result opening with the first, and the
    // result's toolUseResult as the second. Hook, classifier and interrupt texts are not a human's no.
    static readonly string[] HumanRejectionMarkers = [
        "The user doesn't want to proceed with this tool use",
        "User rejected tool use",
    ];

    public static ClaudeJudgeDeclarations Read(string? transcriptPath, string? toolUseId, string? cwd, string? statePath = null) =>
        Read(transcriptPath, toolUseId, cwd, statePath, MaxBytesPerRead);

    internal static ClaudeJudgeDeclarations Read(
            string? transcriptPath, string? toolUseId, string? cwd, string? statePath, long maxBytesPerRead) {
        if (string.IsNullOrEmpty(transcriptPath)) return ClaudeJudgeDeclarations.Unreadable;

        var state     = Load(statePath, transcriptPath);
        var allowance = maxBytesPerRead;
        var changed   = false;
        bool caughtUp;
        try {
            caughtUp = Scan(state, transcriptPath, main: true, cwd, ref allowance, ref changed);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return ClaudeJudgeDeclarations.Unreadable;
        }

        var complete = true;
        try {
            foreach (var file in SubagentTranscripts(transcriptPath))
                caughtUp &= Scan(state, file, main: false, cwd, ref allowance, ref changed);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            complete = false;
        }

        if (changed) Save(statePath, state);

        // Mid-backlog the newest messages and refusals are not read yet, so the read so far proves
        // nothing about the turn or the refusal history.
        if (!caughtUp) return ClaudeJudgeDeclarations.Unreadable;

        var turns = state.Turns.Count > 0
            ? new PolicyJudgeTurnSetV1([.. state.Turns.Select(t => new PolicyJudgeDeclaredTurnV1(t.Id, t.PromptId))], Reference(toolUseId))
            : null;
        return new(turns, Declare(state, complete));
    }

    static IEnumerable<string> SubagentTranscripts(string transcriptPath) {
        var dir = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "",
            Path.GetFileNameWithoutExtension(transcriptPath), "subagents");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl") : [];
    }

    /// <summary>Reads the complete lines appended to <paramref name="path"/> since its cursor, up to
    /// what <paramref name="allowance"/> leaves. A line still being written stays for the next read;
    /// a file shorter than its cursor was replaced, so everything taken from it is dropped and it is
    /// read again whole. False when bytes are left unread for want of allowance.</summary>
    static bool Scan(ClaudeJudgeScanState state, string path, bool main, string? cwd, ref long allowance, ref bool changed) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = state.Cursors.FirstOrDefault(c => c.Path == path)?.Offset ?? 0;
        var length = stream.Length;
        if (length < offset) {
            offset = 0;
            if (main) state.Turns.Clear();
            state.Refusals.RemoveAll(r => r.File == path);
            state.DroppedRefusals.Remove(path);
            changed = true;
        }
        if (length == offset) return true;
        if (allowance <= 0) return false;

        var take = Math.Min(length - offset, allowance);
        var chunk = new byte[take];
        stream.Seek(offset, SeekOrigin.Begin);
        stream.ReadExactly(chunk);
        var end = chunk.AsSpan().LastIndexOf((byte)'\n');
        // A single line longer than the whole allowance can never be taken; skip past it rather
        // than stall on it forever.
        if (end < 0) return take == length - offset || SkipLine(state, path, stream, offset, ref changed);
        allowance -= end + 1;
        changed = true;
        var lines = Lines(chunk.AsSpan(0, end + 1));

        var context = main ? ClaudeTranscriptEvents.Instance.CreateContext("judge", agentId: null) : null;
        var found = new List<ClaudeJudgeScanRefusal>();
        foreach (var line in lines) {
            if (IsRefusalCandidate(line)) CollectRefusals(path, Encoding.UTF8.GetString(line), found);
            if (context is not null && IsTurnCandidate(line) && HumanTurn(Encoding.UTF8.GetString(line), context) is { } turn) {
                state.Turns.Add(turn);
                if (state.Turns.Count > MaxTurns) state.Turns.RemoveAt(0);
            }
        }

        if (found.Count > 0) {
            Resolve(found, lines, cwd);
            if (found.Any(r => r.ToolName is null) && offset > 0) {
                var prefix = new byte[offset];
                stream.Seek(0, SeekOrigin.Begin);
                stream.ReadExactly(prefix);
                Resolve(found, Lines(prefix), cwd);
            }
            state.Refusals.AddRange(found);
            var excess = state.Refusals.Count - MaxRetainedRefusals;
            if (excess > 0) {
                foreach (var dropped in state.Refusals.Take(excess))
                    state.DroppedRefusals[dropped.File] = state.DroppedRefusals.GetValueOrDefault(dropped.File) + 1;
                state.Refusals.RemoveRange(0, excess);
            }
        }

        Advance(state, path, offset + end + 1);
        // Read to the end of the file: all that can remain is a line still being written.
        return take == length - offset;
    }

    static void Advance(ClaudeJudgeScanState state, string path, long offset) {
        state.Cursors.RemoveAll(c => c.Path == path);
        state.Cursors.Add(new(path, offset));
    }

    static bool SkipLine(ClaudeJudgeScanState state, string path, FileStream stream, long offset, ref bool changed) {
        var buffer = new byte[64 * 1024];
        stream.Seek(offset, SeekOrigin.Begin);
        var position = offset;
        int read;
        while ((read = stream.Read(buffer)) > 0) {
            var nl = buffer.AsSpan(0, read).IndexOf((byte)'\n');
            if (nl >= 0) {
                Advance(state, path, position + nl + 1);
                changed = true;
                break;
            }
            position += read;
        }
        return false;
    }

    static List<byte[]> Lines(ReadOnlySpan<byte> bytes) {
        var lines = new List<byte[]>();
        while (bytes.Length > 0) {
            var nl = bytes.IndexOf((byte)'\n');
            var line = nl < 0 ? bytes : bytes[..nl];
            if (line.Length > 0) lines.Add(line.ToArray());
            bytes = nl < 0 ? [] : bytes[(nl + 1)..];
        }
        return lines;
    }

    // Structural tokens only: inside a string value every quote is escaped, so a person's message
    // cannot carry `"type":"tool_result"` unescaped and be skipped by it.
    static bool IsTurnCandidate(ReadOnlySpan<byte> line) =>
        (line.IndexOf("\"user\""u8) >= 0 || line.IndexOf("queued_command"u8) >= 0)
     && line.IndexOf("\"type\":\"tool_result\""u8) < 0;

    // Apostrophe-free fragments of the markers: a JSON writer may escape the apostrophe in
    // "doesn't", and the raw line is matched before it is decoded.
    static bool IsRefusalCandidate(ReadOnlySpan<byte> line) =>
        line.IndexOf("tool_result"u8) >= 0
     && (line.IndexOf("want to proceed with this tool use"u8) >= 0 || line.IndexOf("User rejected tool use"u8) >= 0);

    static ClaudeJudgeScanTurn? HumanTurn(string line, TranscriptContext context) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.Bool("isMeta") == true || root.Bool("isSidechain") == true) return null;
            if (root.Str("uuid") is not { } uuid || !Guid.TryParse(uuid, out var id)) return null;

            foreach (var evt in ClaudeTranscriptEvents.Instance.Project(line, 0, DateTimeOffset.UnixEpoch, context).Events) {
                if (evt.EventId != id || evt.Payload is not UserMessageReceived message) continue;
                if (ClaudeHarnessPayload.IsHarnessPayload(message.Content)) return null;
                return new(uuid, Reference(root.Str("promptId")));
            }
        } catch (JsonException) { }
        return null;
    }

    static void CollectRefusals(string file, string line, List<ClaudeJudgeScanRefusal> found) {
        try {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.Str("type") != "user" || root.Obj("message")?.Arr("content") is not { } blocks) return;
            var promptId = root.Str("promptId");
            var timestamp = root.Str("timestamp");
            // The root describes one result and names none, so it speaks for a line with exactly one.
            var single = blocks.EnumerateArray().Count(b => b.Str("type") == "tool_result") == 1
                ? root.Str("toolUseResult")
                : null;
            foreach (var block in blocks.EnumerateArray()) {
                if (block.Str("type") != "tool_result" || block.Bool("is_error") != true) continue;
                if (block.Str("tool_use_id") is not { Length: > 0 } callId) continue;
                if (!IsHumanRejection(ResultText(block)) && !IsHumanRejection(single)) continue;
                found.Add(new(file, callId, promptId, timestamp, null, null, false));
            }
        } catch (JsonException) { }
    }

    // Joined as the transcript projection joins them, so an empty leading block cannot hide the marker.
    static string? ResultText(JsonElement block) =>
        block.Str("content") ?? (block.Arr("content") is { } parts ? TranscriptText.JoinTextBlocks(parts, "text") : null);

    static bool IsHumanRejection(string? text) {
        if (string.IsNullOrEmpty(text)) return false;
        var span = text.AsSpan().TrimStart();
        if (span.StartsWith("Error: ", StringComparison.Ordinal)) span = span["Error: ".Length..];
        foreach (var marker in HumanRejectionMarkers)
            if (span.StartsWith(marker, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Fills in each refusal's tool and input from its call's line, decoding only lines
    /// that name a refused id. The call precedes its result, so one the new bytes do not hold is in
    /// what an earlier read consumed — read again only then, and a refusal lands rarely.</summary>
    static void Resolve(List<ClaudeJudgeScanRefusal> found, List<byte[]> lines, string? cwd) {
        var ids = found.Select(r => Encoding.UTF8.GetBytes(r.CallId)).ToArray();
        foreach (var line in lines) {
            if (line.AsSpan().IndexOf("\"tool_use\""u8) < 0) continue;
            for (var i = 0; i < found.Count; i++) {
                if (found[i].ToolName is not null || line.AsSpan().IndexOf(ids[i]) < 0) continue;
                if (CallIn(Encoding.UTF8.GetString(line), found[i].CallId, cwd) is { } call)
                    found[i] = found[i] with { ToolName = call.Name, Target = call.Target, Clipped = call.Clipped };
            }
            if (found.TrueForAll(r => r.ToolName is not null)) return;
        }
    }

    static (string Name, string Target, bool Clipped)? CallIn(string line, string callId, string? cwd) {
        try {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.Obj("message")?.Arr("content") is not { } blocks) return null;
            foreach (var block in blocks.EnumerateArray()) {
                if (block.Str("type") != "tool_use" || block.Str("id") != callId || block.Str("name") is not { } name) continue;
                var target = PolicyJudgeTarget.Of(ClaudeActionNormalizer.Normalize(name, block.Prop("input")?.Clone(), cwd));
                return target.Length > MaxTarget ? (name, target[..MaxTarget], true) : (name, target, false);
            }
        } catch (JsonException) { }
        return null;
    }

    static PolicyJudgeRefusalsV1 Declare(ClaudeJudgeScanState state, bool complete) {
        if (state.Refusals.Count > MaxRefusals || state.DroppedRefusals.Values.Any(n => n > 0)) complete = false;

        // Newest first across the main and subagent files; ISO-8601 timestamps order as strings.
        var newest = state.Refusals
            .Select((r, i) => (r, i))
            .OrderByDescending(x => x.r.Timestamp, StringComparer.Ordinal)
            .ThenByDescending(x => x.i)
            .Select(x => x.r)
            .Take(MaxRefusals);

        var entries = new List<PolicyJudgeDeclaredRefusalV1>(MaxRefusals);
        foreach (var r in newest) {
            if (r.CallId.Length > MaxReference) { complete = false; continue; }
            if (r.ToolName is null) {
                complete = false;
                entries.Add(new(r.CallId, "unknown", "", Reference(r.PromptId)));
                continue;
            }
            var tool = r.ToolName;
            if (tool.Length > MaxTool) { tool = tool[..MaxTool]; complete = false; }
            if (r.Clipped) complete = false;
            entries.Add(new(r.CallId, tool, r.Target ?? "", Reference(r.PromptId)));
        }
        return new(complete, PolicyJudgeRefusalsV1.SourceTranscript, [.. entries]);
    }

    static string? Reference(string? value) => value is { Length: > 0 and <= MaxReference } ? value : null;

    /// <summary>A missing, unreadable or foreign state starts the scan from the top: the state only
    /// ever saves work, so losing it costs one full read.</summary>
    static ClaudeJudgeScanState Load(string? statePath, string transcriptPath) {
        var fresh = new ClaudeJudgeScanState { Transcript = transcriptPath };
        if (statePath is null) return fresh;
        try {
            if (File.Exists(statePath)
             && JsonSerializer.Deserialize(File.ReadAllText(statePath), PolicyJsonContext.Default.ClaudeJudgeScanState) is { } s
             && s.Transcript == transcriptPath && s.Cursors is not null && s.Turns is not null && s.Refusals is not null)
                return s;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return fresh;
    }

    // Hooks for parallel tool calls race on this file; each writes a whole, self-consistent state,
    // so the loser's work is only redone by the next read.
    static void Save(string? statePath, ClaudeJudgeScanState state) {
        if (statePath is null) return;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            var tmp = $"{statePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, PolicyJsonContext.Default.ClaudeJudgeScanState));
            File.Move(tmp, statePath, overwrite: true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

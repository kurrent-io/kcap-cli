using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Gemini;

/// <summary>
/// Reads the workspace directory out of Gemini CLI's <c>&lt;session_context&gt;</c> bootstrap.
/// Nothing else on disk names it: the per-project tmp dir is a hash and the recording's header
/// carries only that hash, so the bootstrap's plain-text block is the one machine-readable
/// source a historical import has.
/// <code>
/// - **Workspace Directories:**
///   - /work/demo
/// - **Directory Structure:**
/// </code>
/// The first entry is the launch directory; any others come from <c>--include-directories</c>.
/// Best-effort throughout — a malformed line or a drifted heading reads as "no workspace", which
/// leaves the session exactly as unplaceable as it was before.
/// </summary>
public static class GeminiSessionContext {
    const string OpenTag = "<session_context>";
    const string Heading = "- **Workspace Directories:**";

    /// <summary>
    /// The first workspace directory named by a bootstrap on this raw JSONL line, or null.
    /// Both shapes the bootstrap takes are handled: the <c>$set</c> seed op that opens a
    /// recording, and the bare <c>user</c> message it is re-appended as. The substring test
    /// comes first so the per-line cost stays off every other record in the transcript.
    /// </summary>
    public static string? TryReadWorkspace(string line) {
        if (!line.Contains(OpenTag, StringComparison.Ordinal)) return null;

        try {
            using var doc = JsonDocument.Parse(line);

            foreach (var text in BootstrapTexts(doc.RootElement)) {
                if (WorkspaceFrom(text) is { } dir) return dir;
            }
        } catch { /* malformed → unplaceable */ }

        return null;
    }

    static IEnumerable<string> BootstrapTexts(JsonElement root) {
        if (root.Obj("$set") is { } set && set.Arr("messages") is { } messages) {
            foreach (var message in messages.EnumerateArray()) {
                foreach (var text in TextParts(message)) yield return text;
            }
        }

        foreach (var text in TextParts(root)) yield return text;
    }

    static IEnumerable<string> TextParts(JsonElement message) {
        if (message.Str("content") is { } direct) {
            yield return direct;
            yield break;
        }

        if (message.Arr("content") is { } parts) {
            foreach (var part in parts.EnumerateArray()) {
                if (part.Str("text") is { } text) yield return text;
            }
        }
    }

    static string? WorkspaceFrom(string text) {
        if (!text.StartsWith(OpenTag, StringComparison.Ordinal)) return null;

        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == Heading);
        if (start < 0) return null;

        for (var i = start + 1; i < lines.Length; i++) {
            var raw = lines[i].TrimEnd('\r');
            if (raw.Length == 0) continue;

            // Entries are indented under the heading; the next unindented "- **…**" is a
            // sibling heading, so an empty list stops here rather than yielding it.
            if (!char.IsWhiteSpace(raw[0])) return null;

            var entry = raw.Trim();
            if (!entry.StartsWith("- ", StringComparison.Ordinal)) return null;

            var dir = entry[2..].Trim();
            if (dir.Length > 0) return dir;
        }

        return null;
    }
}

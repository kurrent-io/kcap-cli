namespace Capacitor.Cli.Core.Instructions;

/// <summary>
/// Read-modify-write engine for a harness's markdown agent-instructions file (Copilot's
/// <c>copilot-instructions.md</c>, Gemini's <c>GEMINI.md</c>, <c>AGENTS.md</c>, …). kcap owns only a
/// marker-delimited block inside the file, so user-authored content around it is preserved:
/// non-destructive, idempotent, atomic, fail-closed. The inline markers are self-identifying — no
/// sidecar ownership file is needed (unlike the JSON MCP config).
/// </summary>
public static class AgentInstructionsWriter {
    public enum Change { Unchanged, Updated, Failed }

    // HTML comments so the block is valid Markdown and clearly delimited. Kept stable across
    // versions so an older block is always found and replaced/removed rather than duplicated.
    public const string BeginMarker = "<!-- BEGIN kcap (managed by `kcap` — do not edit inside this block) -->";
    public const string EndMarker   = "<!-- END kcap -->";

    /// <summary>Insert or refresh kcap's block containing <paramref name="body"/> in the file at
    /// <paramref name="path"/>. Creates the file if absent; preserves any content outside the block.</summary>
    public static Change Write(string path, string body) {
        var block = BeginMarker + "\n" + body.Trim('\n') + "\n" + EndMarker;
        try {
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var updated  = ReplaceOrAppendBlock(existing, block);
            if (updated == existing) return Change.Unchanged;

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteAtomic(path, updated);
            return Change.Updated;
        } catch {
            return Change.Failed;
        }
    }

    /// <summary>Strip kcap's block from the file. Deletes the file if nothing but the block (and
    /// whitespace) remains. No-op (Unchanged) if the file or block is absent.</summary>
    public static Change Remove(string path) {
        try {
            if (!File.Exists(path)) return Change.Unchanged;
            var existing = File.ReadAllText(path);
            var stripped = StripBlock(existing);
            if (stripped == existing) return Change.Unchanged;

            if (string.IsNullOrWhiteSpace(stripped)) {
                File.Delete(path);
                return Change.Updated;
            }
            WriteAtomic(path, stripped);
            return Change.Updated;
        } catch {
            return Change.Failed;
        }
    }

    static string ReplaceOrAppendBlock(string content, string block) {
        var (start, end) = FindBlock(content);
        if (start >= 0) return content[..start] + block + content[end..]; // replace in place
        if (content.Length == 0) return block + "\n";                     // fresh file
        var sep = content.EndsWith("\n") ? "\n" : "\n\n";                 // one blank line before ours
        return content + sep + block + "\n";
    }

    static string StripBlock(string content) {
        var (start, end) = FindBlock(content);
        if (start < 0) return content;
        var before = content[..start].TrimEnd('\n');
        var after  = content[end..].TrimStart('\n');
        if (before.Length == 0) return after;
        if (after.Length == 0)  return before + "\n";
        return before + "\n\n" + after;
    }

    // Returns the [start, end) span of the whole block (markers inclusive), or (-1, -1) if absent.
    static (int start, int end) FindBlock(string content) {
        var start = content.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (start < 0) return (-1, -1);
        var end = content.IndexOf(EndMarker, start, StringComparison.Ordinal);
        if (end < 0) return (-1, -1);
        return (start, end + EndMarker.Length);
    }

    static void WriteAtomic(string path, string content) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, content);
        try { File.Move(tmp, path, overwrite: true); }
        catch { try { File.Delete(tmp); } catch { /* best-effort */ } throw; }
    }
}

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// The files a Codex <c>apply_patch</c> envelope names. Each target sits on its own header line —
/// <c>*** Add File:</c>, <c>*** Update File:</c> or <c>*** Delete File:</c> — after
/// <c>*** Begin Patch</c>; the envelope's control lines are never targets themselves.
public static class CodexPatch {
    const string Begin = "*** Begin Patch";

    static readonly string[] FileHeaders = ["*** Add File:", "*** Update File:", "*** Delete File:"];

    /// Null when the text is not a patch envelope; empty when it is one that names no file. A header
    /// must start its line: a hunk's context line opens with a space, so a file whose own text holds
    /// a header line is not read as a target.
    public static IReadOnlyList<string>? Targets(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (lines[first].Trim() != Begin) return null;

        var targets = new List<string>();
        for (var i = first + 1; i < lines.Length; i++) {
            var line   = lines[i].TrimEnd();
            var header = Array.Find(FileHeaders, h => line.StartsWith(h, StringComparison.Ordinal));
            if (header is null) continue;
            var path = line[header.Length..].Trim();
            if (path.Length > 0 && !targets.Contains(path)) targets.Add(path);
        }

        return targets;
    }
}

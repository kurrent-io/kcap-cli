namespace Capacitor.Cli.Core.Skills;

/// <summary>One marker-delimited block in the worktree's <c>info/exclude</c>, which Git resolves to
/// the shared common directory, so a single block covers the repository and all its worktrees. The
/// patterns name only kcap's own directories: a repository may have committed skills beside
/// them.</summary>
public static class SkillsExclusion {
    const string Begin = "# kcap skills (managed) — do not edit between these markers";
    const string End   = "# end kcap skills";

    /// <summary>The trailing shape of every pattern this writes, and the only thing an unterminated
    /// block is allowed to take with it.</summary>
    const string PatternTail = "/" + SkillsMaterializer.OwnedPrefix + "*/";

    public static void Apply(string gitCommonDir, string repoRoot, IReadOnlyList<string> roots) {
        var patterns = roots.Select(r => "/" + Relative(repoRoot, r).Replace(Path.DirectorySeparatorChar, '/')
                                              .TrimStart('/') + PatternTail);
        Rewrite(gitCommonDir, string.Join('\n', [Begin, .. patterns, End]));
    }

    public static void Remove(string gitCommonDir) => Rewrite(gitCommonDir, null);

    static string Relative(string repoRoot, string root) =>
        Path.IsPathRooted(root) ? Path.GetRelativePath(repoRoot, root) : root;

    static void Rewrite(string gitCommonDir, string? block) {
        var path = Path.Combine(gitCommonDir, "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var          text  = File.Exists(path) ? File.ReadAllText(path) : "";
        var          eol   = DominantNewline(text);
        // Split on Git's own line model — LF, with an optional CR before it — so nothing else the
        // user's file contains is read as a line break and rewritten as one.
        List<string> lines = text.Length == 0 ? [] : [.. text.Split('\n').Select(TrimCarriageReturn)];
        var          start = lines.IndexOf(Begin);
        if (start >= 0) lines.RemoveRange(start, BlockLength(lines, start));
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (block is not null) lines.AddRange(block.Split('\n'));
        var rewritten = string.Join(eol, lines) + eol;
        // The file belongs to the user, and this runs on every sync: rewriting identical content
        // would churn its timestamp for nothing.
        if (!string.Equals(rewritten, text, StringComparison.Ordinal)) AtomicFile.Replace(path, rewritten);
    }

    /// <summary>How many lines the block at <paramref name="start"/> occupies. Without a closing
    /// marker — a hand edit, an interrupted write — it covers the marker and the patterns that
    /// follow it, never the rest of a file kcap does not own.</summary>
    static int BlockLength(List<string> lines, int start) {
        var end = lines.IndexOf(End, start);
        if (end >= 0) return end - start + 1;
        var last = start;
        while (last + 1 < lines.Count && lines[last + 1].EndsWith(PatternTail, StringComparison.Ordinal)) last++;
        return last - start + 1;
    }

    static string TrimCarriageReturn(string line) => line.EndsWith('\r') ? line[..^1] : line;

    /// <summary>The line ending to rewrite with: a checkout configured for CRLF must not have its
    /// whole exclude file converted by an edit to one block.</summary>
    static string DominantNewline(string text) {
        var crlf = 0;
        var lf   = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') {
                if (i > 0 && text[i - 1] == '\r') crlf++; else lf++;
            }
        return crlf > lf ? "\r\n" : "\n";
    }
}

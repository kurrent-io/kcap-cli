namespace Capacitor.Cli.Core.Skills;

/// <summary>One marker-delimited block in the worktree's <c>info/exclude</c>, which Git resolves to
/// the shared common directory, so a single block covers the repository and all its worktrees. The
/// patterns name only kcap's own directories: a repository may have committed skills beside
/// them.</summary>
public static class SkillsExclusion {
    const string Begin = "# kcap skills (managed) — do not edit between these markers";
    const string End   = "# end kcap skills";

    public static void Apply(string gitCommonDir, string repoRoot, IReadOnlyList<string> roots) {
        var patterns = roots.Select(r => "/" + Relative(repoRoot, r).Replace(Path.DirectorySeparatorChar, '/')
                                              .TrimStart('/') + "/kcap-*/");
        Rewrite(gitCommonDir, string.Join('\n', [Begin, .. patterns, End]));
    }

    public static void Remove(string gitCommonDir) => Rewrite(gitCommonDir, null);

    static string Relative(string repoRoot, string root) =>
        Path.IsPathRooted(root) ? Path.GetRelativePath(repoRoot, root) : root;

    static void Rewrite(string gitCommonDir, string? block) {
        var path = Path.Combine(gitCommonDir, "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var start = lines.IndexOf(Begin);
        if (start >= 0) {
            var end = lines.IndexOf(End, start);
            lines.RemoveRange(start, (end < 0 ? lines.Count - 1 : end) - start + 1);
        }
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (block is not null) lines.AddRange(block.Split('\n'));
        AtomicFile.Replace(path, string.Join('\n', lines) + "\n");
    }
}

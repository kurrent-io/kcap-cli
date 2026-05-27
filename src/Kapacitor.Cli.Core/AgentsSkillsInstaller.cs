namespace Kapacitor.Cli.Core;

/// <summary>
/// Copies kapacitor skills from a source tree to the user's
/// <c>~/.agents/skills/</c> directory, prefixing each folder name with
/// <c>kapacitor-</c> and rewriting the <c>name:</c> field in SKILL.md to
/// match. Also handles cleanup of legacy <c>~/.codex/skills/kapacitor-*</c>
/// folders left by prior installer versions.
/// </summary>
public static class AgentsSkillsInstaller {
    /// <summary>
    /// Source folder names under <c>kapacitor/skills/</c>. On install each
    /// becomes <c>kapacitor-&lt;name&gt;</c> under the target directory.
    /// Add a new skill here when adding it to <c>kapacitor/skills/</c>.
    /// </summary>
    public static readonly string[] SourceNames = [
        "recap",
        "errors",
        "hide",
        "disable",
        "validate-plan"
    ];

    /// <summary>
    /// Folder names that prior installer versions wrote to
    /// <c>~/.codex/skills/</c>. Fixed list — only these names are removed
    /// during legacy cleanup; user-authored skills are never touched.
    /// </summary>
    public static readonly string[] LegacyCodexSkillNames = [
        "kapacitor-recap",
        "kapacitor-errors",
        "kapacitor-hide",
        "kapacitor-disable",
        "kapacitor-validate-plan"
    ];

    public static bool Install(string sourceDir, string targetDir) {
        if (!Directory.Exists(sourceDir)) return false;

        var missing = SourceNames
            .Where(n => !Directory.Exists(Path.Combine(sourceDir, n)))
            .ToList();
        if (missing.Count > 0) {
            Console.Error.WriteLine(
                $"Cannot install agent skills: missing source folder(s) under {sourceDir}: "
                + string.Join(", ", missing));
            return false;
        }

        try {
            Directory.CreateDirectory(targetDir);

            foreach (var name in SourceNames) {
                var src    = Path.Combine(sourceDir, name);
                var prefix = "kapacitor-" + name;
                var dst    = Path.Combine(targetDir, prefix);

                if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                CopyDirectoryWithFrontmatterRewrite(src, dst, prefix);
            }
            return true;
        } catch (Exception ex) {
            Console.Error.WriteLine($"Could not install agent skills: {ex.Message}");
            return false;
        }
    }

    static void CopyDirectoryWithFrontmatterRewrite(string source, string destination, string targetName) {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source)) {
            var fileName = Path.GetFileName(file);
            var dstPath  = Path.Combine(destination, fileName);
            if (fileName == "SKILL.md") {
                File.WriteAllText(dstPath, RewriteNameFrontmatter(File.ReadAllText(file), targetName));
            } else {
                File.Copy(file, dstPath, overwrite: true);
            }
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            CopyDirectoryWithFrontmatterRewrite(dir, Path.Combine(destination, Path.GetFileName(dir)), targetName);
        }
    }

    /// <summary>
    /// Replaces the first <c>name:</c> line inside the YAML frontmatter
    /// block of a SKILL.md. The block is delimited by lines containing
    /// only <c>---</c>. If no frontmatter is found, the content is
    /// returned unchanged.
    /// </summary>
    internal static string RewriteNameFrontmatter(string content, string newName) {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length < 2 || lines[0].Trim() != "---") return content;

        for (var i = 1; i < lines.Length; i++) {
            if (lines[i].Trim() == "---") break;
            if (lines[i].StartsWith("name:", StringComparison.Ordinal)) {
                lines[i] = $"name: {newName}";
                break;
            }
        }
        return string.Join('\n', lines);
    }
}

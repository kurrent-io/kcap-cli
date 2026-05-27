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
            .Where(n => !File.Exists(Path.Combine(sourceDir, n, "SKILL.md")))
            .ToList();
        if (missing.Count > 0) {
            Console.Error.WriteLine(
                $"Cannot install agent skills: missing SKILL.md for skill(s) under {sourceDir}: "
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

    /// <summary>
    /// Deletes every <c>kapacitor-&lt;name&gt;</c> folder this installer owns
    /// from <paramref name="targetDir"/>. User-authored folders are left alone.
    /// Returns a <see cref="RemovalResult"/> indicating whether any folder was
    /// removed and whether any deletion failed.
    /// </summary>
    public static RemovalResult Remove(string targetDir) {
        if (!Directory.Exists(targetDir)) return new RemovalResult(false, false);

        var removed = false;
        var errors  = false;
        foreach (var name in SourceNames) {
            var dst = Path.Combine(targetDir, "kapacitor-" + name);
            if (!Directory.Exists(dst)) continue;
            try {
                Directory.Delete(dst, recursive: true);
                removed = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"Could not remove agent skill 'kapacitor-{name}': {ex.Message}");
                errors = true;
            }
        }
        return new RemovalResult(removed, errors);
    }

    /// <summary>
    /// Removes legacy <c>kapacitor-*</c> folders that prior installer versions
    /// wrote to <c>~/.codex/skills/</c>. List of folder names is fixed
    /// (<see cref="LegacyCodexSkillNames"/>); user-authored skills are never
    /// touched. If the parent directory becomes empty, it is removed too.
    /// Returns a <see cref="RemovalResult"/> indicating whether any folder was
    /// removed and whether any deletion failed.
    /// </summary>
    public static RemovalResult CleanLegacyCodexSkills(string legacySkillsDir) {
        if (!Directory.Exists(legacySkillsDir)) return new RemovalResult(false, false);

        var removed = false;
        var errors  = false;
        foreach (var name in LegacyCodexSkillNames) {
            var dst = Path.Combine(legacySkillsDir, name);
            if (!Directory.Exists(dst)) continue;
            try {
                Directory.Delete(dst, recursive: true);
                Console.Out.WriteLine($"Removed legacy Codex skill folder: {dst}");
                removed = true;
            } catch (Exception ex) {
                Console.Error.WriteLine($"Could not remove legacy Codex skill '{name}': {ex.Message}");
                errors = true;
            }
        }

        // Clean up empty parent.
        try {
            if (Directory.Exists(legacySkillsDir) && !Directory.EnumerateFileSystemEntries(legacySkillsDir).Any()) {
                Directory.Delete(legacySkillsDir);
            }
        } catch {
            // Best effort — leaving an empty dir behind isn't harmful.
        }

        return new RemovalResult(removed, errors);
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

/// <summary>
/// Describes the outcome of a removal operation performed by
/// <see cref="AgentsSkillsInstaller"/>.
/// </summary>
public readonly record struct RemovalResult(bool RemovedAny, bool HadErrors);

using System.Text.Json;

namespace Capacitor.Cli.Core.Skills;

public sealed record LegacyMigrationPlan(
    string ManifestPath, IReadOnlyList<string> Delete, IReadOnlyList<string> Keep);

/// <summary>Retires the user-global copies one repository owns. A global path carries no repository
/// identity, so two repositories can own the same directory — a project-homed skill does exactly
/// that — and deleting one repository's copy would take the other's with it.</summary>
public static class SkillsLegacyMigration {
    public static LegacyMigrationPlan Plan(
            string configRoot, string repoHash, string targetKey, SkillsIdentity current) {
        var mine         = Path.Combine(configRoot, "skills", repoHash, targetKey, "manifest.json");
        var mineManifest = Load(mine);
        var owned        = mineManifest?.Skills?.Select(e => e.Path).ToList() ?? [];
        var retired      = mineManifest?.Identity;
        var delete = new List<string>();
        var keep   = new List<string>();

        foreach (var path in owned) {
            var others = Others(configRoot, mine, path);
            // A remaining owner under the same retired identity is not serving it either.
            var liveOwner = others.Any(m => m.Identity is null
                                            || Equals(m.Identity, current)
                                            || !Equals(m.Identity, retired));
            if (others.Count == 0 || !liveOwner) delete.Add(path); else keep.Add(path);
        }
        return new LegacyMigrationPlan(mine, delete, keep);
    }

    static List<SkillsManifest> Others(string configRoot, string minePath, string path) {
        var skills = Path.Combine(configRoot, "skills");
        if (!Directory.Exists(skills)) return [];
        return [.. Directory.EnumerateFiles(skills, "manifest.json", SearchOption.AllDirectories)
            .Where(f => !string.Equals(f, minePath, StringComparison.Ordinal))
            .Select(Load).OfType<SkillsManifest>()
            .Where(m => (m.Skills ?? []).Any(e => string.Equals(e.Path, path, StringComparison.Ordinal)))];
    }

    static SkillsManifest? Load(string path) {
        try {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.SkillsManifest)
                : null;
        } catch {
            return null;
        }
    }
}

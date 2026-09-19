using System.Text.Json;

namespace Capacitor.Cli.Core.Skills;

/// <summary>What migration may do to one repository's legacy ledger. <paramref name="Unreadable"/>
/// means the ledger is there but will not parse: it names paths nothing else can, so an empty
/// <paramref name="Delete"/> beside an empty <paramref name="Keep"/> is not "owns nothing".</summary>
public sealed record LegacyMigrationPlan(
    string ManifestPath, IReadOnlyList<string> Delete, IReadOnlyList<string> Keep, bool Unreadable = false);

/// <summary>Retires the user-global copies one repository owns. A global path carries no repository
/// identity, so two repositories can own the same directory — a project-homed skill does exactly
/// that — and deleting one repository's copy would take the other's with it.</summary>
public static class SkillsLegacyMigration {
    /// <summary>The user-global ledger for one (repo, target). Built here for every caller: the
    /// sibling scan below excludes a repository from its own candidate list by comparing this path,
    /// and a repository that failed to recognise its own ledger would read itself as another owner
    /// of everything it owns, stopping migration with nothing failing.</summary>
    public static string ManifestPathFor(string configRoot, string repoHash, string targetKey) =>
        Path.Combine(configRoot, "skills", repoHash, targetKey, "manifest.json");

    public static LegacyMigrationPlan Plan(
            string configRoot, string repoHash, string targetKey, SkillsIdentity current) {
        var mine         = ManifestPathFor(configRoot, repoHash, targetKey);
        var mineManifest = Load(mine);
        if (mineManifest is null && File.Exists(mine))
            return new LegacyMigrationPlan(mine, [], [], Unreadable: true);
        var owned      = mineManifest?.Skills?.Select(e => e.Path).ToList() ?? [];
        var retired    = mineManifest?.Identity;
        var candidates = Candidates(configRoot, mine);

        // A sibling that exists but will not parse could be hiding the only other owner of any
        // owned path; nothing can be proven safe to delete until it is readable or gone.
        if (candidates.Any(f => File.Exists(f) && Load(f) is null))
            return new LegacyMigrationPlan(mine, [], owned);

        var delete = new List<string>();
        var keep   = new List<string>();

        foreach (var path in owned) {
            var others = Others(candidates, path);
            // A remaining owner under the same retired identity is not serving it either.
            var liveOwner = others.Any(m => m.Identity is null
                                            || Equals(m.Identity, current)
                                            || !Equals(m.Identity, retired));
            if (others.Count == 0 || !liveOwner) delete.Add(path); else keep.Add(path);
        }
        return new LegacyMigrationPlan(mine, delete, keep);
    }

    static List<string> Candidates(string configRoot, string minePath) {
        var skills = Path.Combine(configRoot, "skills");
        if (!Directory.Exists(skills)) return [];
        return [.. Directory.EnumerateFiles(skills, "manifest.json", SearchOption.AllDirectories)
            .Where(f => !PathComparison.Equal(f, minePath))];
    }

    static List<SkillsManifest> Others(List<string> candidates, string path) =>
        [.. candidates.Select(Load).OfType<SkillsManifest>()
            .Where(m => (m.Skills ?? []).Any(e => PathComparison.Equal(e.Path, path)))];

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

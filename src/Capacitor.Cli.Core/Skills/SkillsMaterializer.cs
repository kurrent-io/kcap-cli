using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Skills;

/// <summary>
/// The file half of skills materialization, harness-neutral: how a slug maps to a directory under
/// a given skills root, and the write/prune/drift operations. Which roots exist and which vendor
/// each fetches as is the target catalog's business.
/// </summary>
public static class SkillsMaterializer {
    /// <summary>What marks a directory as kcap's inside a skills root the repository also uses. The
    /// write, the prune guard and the Git exclusion glob all spell it through this constant: a
    /// divergence would stop the exclusion matching what the writer creates, with nothing
    /// failing.</summary>
    public const string OwnedPrefix = "kcap-";

    // The server's slug is already doc-id-anchored and unique; the prefix namespaces the
    // materialized set inside a shared skills root.
    public static string SkillDirFor(string root, string slug) => Path.Combine(root, OwnedPrefix + slug);

    public static string SkillFileFor(string dir) => Path.Combine(dir, "SKILL.md");

    public static string FileHash(string rendered) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rendered)));

    /// <summary>A manifest entry whose materialized file is missing or edited. A drifted entry means
    /// metadata alone cannot prove the skill is served — the snapshot must be re-applied.</summary>
    public static bool HasDrifted(SkillsManifestEntry entry) {
        var file = SkillFileFor(entry.Path);
        if (entry.FileHash is null || !File.Exists(file)) return true;
        try {
            return FileHash(File.ReadAllText(file)) != entry.FileHash;
        } catch {
            return true;
        }
    }

    /// <summary>Writes one skill, refusing a destination that leaves the anchor through a link: a
    /// vendor directory inside the repository may be a symlink to the user-global tree, which would
    /// publish repository content globally again.</summary>
    public static bool Write(string root, string anchor, SkillSnapshotItem item) {
        var dir = SkillDirFor(root, item.Slug);
        if (!CanonicalPath.IsWithin(dir, anchor)) return false;
        Directory.CreateDirectory(dir);
        var file = SkillFileFor(dir);
        if (File.Exists(file) && new FileInfo(file).LinkTarget is not null) return false;
        AtomicFile.Replace(file, SkillsSyncPlanner.RenderSkillFile(item));
        return true;
    }

    /// <summary>Deletes one owned directory: a DIRECT kcap-* child of the given root that also
    /// resolves inside the anchor.</summary>
    public static bool Prune(string root, string anchor, string path) {
        var full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root), StringComparison.Ordinal)) return false;
        if (!Path.GetFileName(full).StartsWith(OwnedPrefix, StringComparison.Ordinal)) return false;
        if (!CanonicalPath.IsWithin(full, anchor)) return false;
        if (!Directory.Exists(full)) return false;
        Directory.Delete(full, recursive: true);
        return true;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Lock names for skills materialization. One pair ever nests — migration outside the
/// per-worktree manifest lock, never the other way round. The repository lock is taken alone,
/// before any target starts and while nothing else is held, so it nests with neither. No shared
/// lock is ever held across a network request.</summary>
public static class SkillsLocks {
    /// <summary>One key for the machine: a legacy global directory can be owned by two repositories,
    /// so two keys would let each observe the other as the remaining owner and neither delete it.
    /// </summary>
    public const string Migration = "skills/legacy-migration";

    public static string Repository(string repoHash) => $"skills/{repoHash}/repository";

    /// <summary>One key per (worktree, target). The git directory is reduced to the form two
    /// spellings of it share before it is hashed: without that, two launches through different
    /// casings of one checkout take two locks and do not serialize at all.</summary>
    public static string Manifest(string gitDir, string targetKey) =>
        $"skills/manifest/{Digest(PathComparison.Key(gitDir))}/{targetKey}";

    static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}

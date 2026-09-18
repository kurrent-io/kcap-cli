using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Lock names for skills materialization. Always acquired in this order — migration, then
/// repository, then manifest — and no shared lock is ever held across a network request.</summary>
public static class SkillsLocks {
    /// <summary>One key for the machine: a legacy global directory can be owned by two repositories,
    /// so two keys would let each observe the other as the remaining owner and neither delete it.
    /// </summary>
    public const string Migration = "skills/legacy-migration";

    public static string Repository(string repoHash) => $"skills/{repoHash}/repository";

    public static string Manifest(string gitDir, string targetKey) =>
        $"skills/manifest/{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(gitDir)))[..16]}/{targetKey}";
}

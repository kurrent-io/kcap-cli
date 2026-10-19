using System.Security.Cryptography;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Decides whether a unit's recorded root is this root by filesystem identity. Spelling
/// cannot decide it: a case-insensitive volume and symlinked ancestors give one directory many names,
/// and a case-sensitive volume gives one spelling's twin a different directory.</summary>
static class LocalRootIdentity {
    /// <summary>With <paramref name="mayWrite"/>, call while holding the lifecycle lease: the probe writes into the root.
    /// Without it, two spellings that resolve differently but both exist are <see cref="RootMatch.Unknown"/>.</summary>
    public static RootMatch Compare(LocalRoot root, string other, bool mayWrite = true) {
        if (CanonicalPath.TryResolve(root.Directory, out var mine)
         && CanonicalPath.TryResolve(other, out var theirs)
         && string.Equals(mine, theirs, StringComparison.Ordinal))
            return RootMatch.Same;

        try {
            if (!Present(other)) return RootMatch.Different;
            if (!mayWrite) return RootMatch.Unknown;

            // A nonce created here is visible under `other` only if both names reach one directory.
            var name  = $".identity-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}";
            var probe = Path.Combine(root.Directory, name);

            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            try {
                return Present(Path.Combine(other, name)) ? RootMatch.Same : RootMatch.Different;
            } finally {
                File.Delete(probe);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return RootMatch.Unknown;
        }
    }

    // Exists() reads a permission error as absence, which would call an unreadable root a different one.
    static bool Present(string path) {
        try {
            File.GetAttributes(path);
            return true;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return false;
        }
    }

    public static OwnerDecision Decide(LocalRoot root, UnitOwner owner, bool mayWrite = true) {
        var matches = new List<RootMatch>(2);

        foreach (var reading in new[] { owner.Loaded, owner.OnDisk }) {
            switch (reading) {
                case OwnerReading.Absent:
                    break;
                case OwnerReading.Root r:
                    matches.Add(Compare(root, r.Path, mayWrite));
                    break;
                default:
                    return OwnerDecision.Unknown;
            }
        }

        if (matches.Count == 0) return OwnerDecision.NotOurs;
        if (matches.All(m => m == RootMatch.Same)) return OwnerDecision.Ours;
        if (matches.All(m => m == RootMatch.Different)) return OwnerDecision.NotOurs;

        return OwnerDecision.Unknown;
    }
}

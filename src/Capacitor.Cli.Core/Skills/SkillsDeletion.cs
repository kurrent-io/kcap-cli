namespace Capacitor.Cli.Core.Skills;

public enum SkillDeletionResult {
    /// <summary>The file and its directory are gone; the row has nothing left to claim.</summary>
    Removed,

    /// <summary>The file is gone and the directory stays, because it holds something we did not
    /// write.</summary>
    Settled,

    /// <summary>Nothing was deleted and nothing was established: a guard refused the path, the file
    /// is a link, or it could not be read. Whatever this row holds is still the best evidence there
    /// is, and a later run retries.</summary>
    Refused,

    /// <summary>The file was read, and no receipt this row holds accounts for it. Unlike a refusal
    /// this is an answer, and the only one that may cost a row its evidence.</summary>
    Unvouched,
}

/// <summary>
/// The floor the whole ownership record rests on: only the managed file, and only what we confirmed
/// writing. A bookkeeping mistake that survives this leaves a stale directory rather than removing
/// a file we never wrote.
///
/// <para>Two limits are accepted rather than claimed away. A byte-identical copy of a managed file,
/// at a path we own, is indistinguishable from ours and will be deleted. And hashing a file and
/// then unlinking it are two operations, so an editor saving in between loses that save — the locks
/// exclude other kcap runs and nothing else.</para>
/// </summary>
public static class SkillsDeletion {
    /// <summary>Deletes <c>SKILL.md</c> and only <c>SKILL.md</c>, and only when it hashes to one of
    /// the receipts this row holds, then removes the containing directory only if it is empty.
    /// Never recursive.</summary>
    public static SkillDeletionResult Delete(OwnedSkillRow row, SkillAuthority authority) {
        if (Gone(row.Path)) return SkillDeletionResult.Removed;
        if (Admit(row.Path, authority) is not { } full) return SkillDeletionResult.Refused;

        var file = new FileInfo(SkillsMaterializer.SkillFileFor(full));

        if (file.Exists) {
            if (file.LinkTarget is not null) return SkillDeletionResult.Refused;
            if (SkillsMaterializer.HashOf(file.FullName) is not { } hash) return SkillDeletionResult.Refused;
            if (!row.Receipts.Any(r => r.Matches(hash))) return SkillDeletionResult.Unvouched;

            File.Delete(file.FullName);
        }

        return Retire(full);
    }

    /// <summary>Gives up a destination that was never written to: the directory goes if it is
    /// empty, and stays — holding something we did not write — if it is not.</summary>
    public static SkillDeletionResult Release(OwnedSkillRow row, SkillAuthority authority) =>
        Gone(row.Path) ? SkillDeletionResult.Removed
        : Admit(row.Path, authority) is { } full ? Retire(full)
        : SkillDeletionResult.Refused;

    /// <summary>Nothing there is nothing to guard, and a deletion already carried out.</summary>
    static bool Gone(string path) => !Directory.Exists(Path.GetFullPath(path));

    static SkillDeletionResult Retire(string full) {
        if (Directory.EnumerateFileSystemEntries(full).Any()) return SkillDeletionResult.Settled;

        Directory.Delete(full, recursive: false);

        return SkillDeletionResult.Removed;
    }

    /// <summary>The standing containment and link checks: a direct kcap-owned child of the root
    /// that authorises it, resolving inside that root's own boundary.</summary>
    static string? Admit(string path, SkillAuthority authority) {
        var full = Path.GetFullPath(path);

        return PathComparison.Equal(Path.GetDirectoryName(full), Path.GetFullPath(authority.Root))
               && Path.GetFileName(full).StartsWith(SkillsMaterializer.OwnedPrefix, PathComparison.Comparison)
               && CanonicalPath.IsWithin(full, authority.Boundary)
            ? full
            : null;
    }
}

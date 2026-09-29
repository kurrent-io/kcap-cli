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
        if (Presence(row.Path) is not PathPresence.Present) return Outcome(row.Path);
        if (Admit(row.Path, authority) is not { } full) return SkillDeletionResult.Refused;

        var managed = SkillsMaterializer.SkillFileFor(full);

        switch (PathExistence.OfFile(managed)) {
            case PathPresence.Indeterminate:
                return SkillDeletionResult.Refused;
            case PathPresence.Present:
                var file = new FileInfo(managed);

                if (file.LinkTarget is not null) return SkillDeletionResult.Refused;
                if (SkillsMaterializer.HashOf(managed) is not { } hash) return SkillDeletionResult.Refused;
                if (!row.Receipts.Any(r => r.Matches(hash))) return SkillDeletionResult.Unvouched;

                File.Delete(managed);
                break;
        }

        return Retire(full);
    }

    /// <summary>Gives up a destination that was never written to: the directory goes if it is
    /// empty, and stays — holding something we did not write — if it is not.</summary>
    public static SkillDeletionResult Release(OwnedSkillRow row, SkillAuthority authority) =>
        Presence(row.Path) is not PathPresence.Present ? Outcome(row.Path)
        : Admit(row.Path, authority) is { } full ? Retire(full)
        : SkillDeletionResult.Refused;

    static PathPresence Presence(string path) => PathExistence.OfDirectory(Path.GetFullPath(path));

    /// <summary>Nothing there is nothing to guard, and a deletion already carried out. A directory
    /// that would not answer is neither.</summary>
    static SkillDeletionResult Outcome(string path) =>
        Presence(path) == PathPresence.Missing ? SkillDeletionResult.Removed : SkillDeletionResult.Refused;

    static SkillDeletionResult Retire(string full) {
        bool empty;

        try {
            empty = !Directory.EnumerateFileSystemEntries(full).Any();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Emptiness could not be established, so nothing here may settle a row or remove a
            // directory.
            return SkillDeletionResult.Refused;
        }

        if (!empty) return SkillDeletionResult.Settled;

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

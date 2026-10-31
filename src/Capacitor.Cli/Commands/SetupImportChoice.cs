namespace Capacitor.Cli.Commands;

/// <summary>
/// One project setup's import offers to include: a repository, or a folder whose sessions matched none.
/// Every one starts included, so a run nobody narrows imports everything; one left out is left out of
/// this import only, through <see cref="ImportSkip"/>.
/// </summary>
/// <param name="Included">Whether it starts ticked: false only when a <c>--skip-import-*</c> flag named it.</param>
internal sealed record SetupImportChoice(
    string Label, (string Owner, string Name)? Repo, string? Folder, int Sessions, bool Included) {

    /// <summary>Repositories first, newest first, then the named folders, most sessions first. Sessions with
    /// no folder recorded cannot be named by a flag, so they are not a choice: they import with the rest.</summary>
    internal static IReadOnlyList<SetupImportChoice> From(ImportDiscoverySummary summary, ImportSkip preset) {
        var repos = summary.Repos.Select(r => new SetupImportChoice(
            $"{r.Owner}/{r.Name} · {Count(r.SessionCount)}", (r.Owner, r.Name), null, r.SessionCount,
            !preset.Covers((r.Owner, r.Name), null)));

        var folders = summary.UnmatchedFolders.Where(f => f.Folder is not null).Select(f => new SetupImportChoice(
            $"{f.Folder} · {Count(f.Sessions)} · no repository", null, f.Folder, f.Sessions,
            !preset.Covers((null, null), f.Folder)));

        return [.. repos, .. folders];
    }

    /// <summary>What a run leaves out: every choice not in <paramref name="kept"/>, plus whatever a flag
    /// named that discovery did not offer, so a flag is never quietly dropped. A flagged choice ticked
    /// again is imported.</summary>
    internal static ImportSkip SkipFor(IReadOnlyList<SetupImportChoice> all, IReadOnlyList<SetupImportChoice> kept, ImportSkip preset) {
        var left    = all.Where(c => !kept.Contains(c)).ToList();
        var offered = new ImportSkip(
            [.. all.Where(c => c.Repo is not null).Select(c => c.Repo!.Value)],
            [.. all.Where(c => c.Folder is not null).Select(c => c.Folder!)]);

        return new ImportSkip(
            [
                .. preset.Repos.Where(r => !offered.Covers(r, null)),
                .. left.Where(c => c.Repo is not null).Select(c => c.Repo!.Value),
            ],
            [
                .. preset.Folders.Where(f => !offered.Covers((null, null), f)),
                .. left.Where(c => c.Folder is not null).Select(c => c.Folder!),
            ]);
    }

    static string Count(int sessions) => sessions == 1 ? "1 session" : $"{sessions} sessions";
}

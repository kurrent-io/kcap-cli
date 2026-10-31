namespace Capacitor.Cli.Commands;

/// <summary>
/// Repositories and folders one import run leaves out, whatever its scope. Nothing here is saved:
/// recording from now on, and every later import, follow <c>excluded_repos</c> and <c>kcap ignore</c>
/// alone, which stay authoritative whether or not a project is listed here.
/// </summary>
public sealed record ImportSkip {
    public static ImportSkip None { get; } = new([], []);

    public ImportSkip(IReadOnlyList<(string Owner, string Name)> repos, IReadOnlyList<string> folders) {
        Repos   = [.. repos.Distinct(ImportScope.RepoComparer)];
        Folders = [.. folders.Select(ImportScope.FolderKey).Distinct(StringComparer.Ordinal)];
    }

    public IReadOnlyList<(string Owner, string Name)> Repos { get; }

    /// <summary>Folders as <c>kcap import --discover</c> names them, each an <see cref="ImportScope.FolderKey"/>.</summary>
    public IReadOnlyList<string> Folders { get; }

    public bool IsEmpty => Repos.Count == 0 && Folders.Count == 0;

    /// <summary>Whether a session in <paramref name="repo"/> that ran in <paramref name="folder"/> is left out.</summary>
    public bool Covers((string? Owner, string? Name) repo, string? folder) =>
        (repo is { Owner: { } owner, Name: { } name } && Repos.Contains((owner, name), ImportScope.RepoComparer))
     || (folder is not null && Folders.Contains(ImportScope.FolderKey(folder), StringComparer.Ordinal));

    /// <summary>Every entry, the way the flags spell them.</summary>
    public IEnumerable<string> Labels => Repos.Select(r => $"{r.Owner}/{r.Name}").Concat(Folders);

    /// <summary>The <c>kcap import</c> arguments that carry this skip to another process.</summary>
    public IEnumerable<string> ImportArgs =>
        Repos.SelectMany(r => (string[])["--skip-repo", $"{r.Owner}/{r.Name}"])
            .Concat(Folders.SelectMany(f => (string[])["--skip-folder", f]));

    public bool Equals(ImportSkip? other) =>
        other is not null
     && Repos.Count == other.Repos.Count
     && !Repos.Except(other.Repos, ImportScope.RepoComparer).Any()
     && Folders.Count == other.Folders.Count
     && !Folders.Except(other.Folders, StringComparer.Ordinal).Any();

    public override int GetHashCode() =>
        Folders.Aggregate(
            Repos.Aggregate(0, (acc, r) => acc ^ ImportScope.RepoComparer.GetHashCode(r)),
            (acc, f) => acc ^ StringComparer.Ordinal.GetHashCode(f));
}

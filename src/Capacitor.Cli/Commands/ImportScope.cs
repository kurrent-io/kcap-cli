namespace Capacitor.Cli.Commands;

/// <summary>
/// Selected import scope, resolved from CLI flags or the interactive picker.
/// </summary>
public abstract record ImportScope {
    /// <summary>
    /// Owner and name compared the way git remotes are: case-insensitively, both halves. Shared so a
    /// scope's own equality and the filter's lookup can never disagree about what "the same repo" is.
    /// </summary>
    public static readonly IEqualityComparer<(string Owner, string Name)> RepoComparer = new RepoEquality();

    public sealed record All : ImportScope;
    public sealed record Org (string OrgLogin) : ImportScope;

    /// <summary>One or more repositories, and folders outside any repository, matched as a set: a session
    /// is in scope when its repository or its folder is.</summary>
    public sealed record Repo : ImportScope {
        public Repo(IReadOnlyList<(string Owner, string Name)> repos, IReadOnlyList<string>? folders = null) {
            // Empty would have to mean either "everything" or "nothing", and a scope that silently
            // means one when the caller meant the other is worth refusing outright.
            if (repos.Count == 0 && (folders is null || folders.Count == 0))
                throw new ArgumentException("A repo scope needs at least one repository or folder.", nameof(repos));

            // Stored deduped, so this really is a set: naming a repo twice is one selection, and
            // equality and hashing below can agree without reasoning about multiplicity.
            Repos   = [.. repos.Distinct(RepoComparer)];
            Folders = [.. (folders ?? []).Select(FolderKey).Distinct(StringComparer.Ordinal)];
        }

        public Repo(string owner, string name) : this([(owner, name)]) { }

        public IReadOnlyList<(string Owner, string Name)> Repos { get; }

        /// <summary>Folders as <c>kcap import --discover</c> names them, each a <see cref="FolderKey"/>.</summary>
        public IReadOnlyList<string> Folders { get; }

        // A record over a list compares by reference, so equality is spelled out. Sound because both
        // lists are deduped: set equality and an order-insensitive XOR agree only without repeats.
        public bool Equals(Repo? other) =>
            other is not null
         && Repos.Count == other.Repos.Count
         && !Repos.Except(other.Repos, RepoComparer).Any()
         && Folders.Count == other.Folders.Count
         && !Folders.Except(other.Folders, StringComparer.Ordinal).Any();

        public override int GetHashCode() =>
            Folders.Aggregate(
                Repos.Aggregate(0, (acc, r) => acc ^ RepoComparer.GetHashCode(r)),
                (acc, f) => acc ^ StringComparer.Ordinal.GetHashCode(f));
    }

    /// <summary>A folder the way discovery groups sessions by it: ordinal, without a trailing separator.</summary>
    public static string FolderKey(string folder) {
        var trimmed = folder.TrimEnd('/', '\\');

        return trimmed.Length == 0 ? folder : trimmed;
    }

    private ImportScope() { }

    sealed class RepoEquality : IEqualityComparer<(string Owner, string Name)> {
        public bool Equals((string Owner, string Name) a, (string Owner, string Name) b) =>
            string.Equals(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase)
         && string.Equals(a.Name,  b.Name,  StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Owner, string Name) r) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(r.Owner),
                StringComparer.OrdinalIgnoreCase.GetHashCode(r.Name));
    }
}

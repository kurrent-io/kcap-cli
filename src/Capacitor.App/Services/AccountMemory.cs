using System.Collections.Frozen;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// The chosen account per host, vendor and repository, kept in <see cref="AppState.AccountByRepo"/>
/// under "host/vendor:repo". A key splits on its first '/' and the first ':' after it — host ids
/// are GUID "N" strings and vendor tokens hold neither — so a repo path may carry both. The repo
/// part is "" for the not-yet-in-a-repository target (HomeViewModel.ScratchRepoPath).
public static class AccountMemory {
    public static string KeyFor(string hostId, string vendor, string repoPath) =>
        $"{hostId}/{vendor}:{PlatformPaths.Normalize(repoPath)}";

    /// The repo part is compared under PlatformPaths.Comparer on read: System.Text.Json rebuilds
    /// the dictionary with an ordinal comparer on load, so a comparer set at write time would not
    /// survive the round-trip.
    public static string? Lookup(IReadOnlyDictionary<string, string>? byRepo, string hostId, string vendor, string repoPath) {
        if (byRepo is null) return null;

        foreach (var entry in byRepo)
            if (Matches(entry.Key, hostId, vendor, repoPath))
                return entry.Value;

        return null;
    }

    /// A null accountId clears the choice and adds nothing.
    public static IReadOnlyDictionary<string, string> With(
        IReadOnlyDictionary<string, string>? existing, string hostId, string vendor, string repoPath, string? accountId) {
        var next = new Dictionary<string, string>();
        if (existing is not null)
            foreach (var entry in existing)
                if (!Matches(entry.Key, hostId, vendor, repoPath))
                    next[entry.Key] = entry.Value;

        if (accountId is not null) next[KeyFor(hostId, vendor, repoPath)] = accountId;
        return next.Count == 0 ? FrozenDictionary<string, string>.Empty : next;
    }

    static bool Matches(string key, string hostId, string vendor, string repoPath) {
        var slash = key.IndexOf('/');
        if (slash < 0) return false;

        var colon = key.IndexOf(':', slash + 1);
        if (colon < 0) return false;

        return key.AsSpan(0, slash).SequenceEqual(hostId)
            && key.AsSpan(slash + 1, colon - slash - 1).SequenceEqual(vendor)
            && PlatformPaths.Comparer.Equals(key[(colon + 1)..], repoPath);
    }
}

using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.Services;

public static class AccountRows {
    static readonly char[] Separators = ['/', '\\'];

    /// A snapshot carries no row for a default directory nobody registered, so it is appended
    /// here. A deserialized snapshot can hold null arrays; they read as empty.
    public static IReadOnlyList<AccountRow> For(AccountsSnapshotDto snapshot, string vendor) {
        var fallback = (snapshot.Defaults ?? []).FirstOrDefault(d => string.Equals(d.Vendor, vendor, StringComparison.OrdinalIgnoreCase));

        var rows = (snapshot.Accounts ?? [])
            .Where(a => string.Equals(a.Vendor, vendor, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Label, StringComparer.Ordinal)
            .ThenBy(a => a.Directory, StringComparer.Ordinal)
            .Select(a => new AccountRow(
                a.Id, a.Vendor, a.Directory, a.Label, a.RecordingState,
                fallback is not null && string.Equals(a.DirectoryKey, fallback.DirectoryKey, StringComparison.Ordinal)))
            .ToList();

        if (fallback is not null && !rows.Any(r => r.IsDefault))
            rows.Add(new AccountRow(null, vendor, fallback.Directory, LastSegment(fallback.Directory), RecordingStates.Token(RecordingState.NotWired), true));

        return rows;
    }

    // Split on both separators: the daemon's host may not run the platform this app does.
    internal static string LastSegment(string directory) {
        var trimmed = directory.TrimEnd(Separators);
        return trimmed[(trimmed.LastIndexOfAny(Separators) + 1)..];
    }
}

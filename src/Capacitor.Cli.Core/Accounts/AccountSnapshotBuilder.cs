using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>Builds what the daemon publishes of a host's accounts. There is no row for a default
/// directory: a reader derives the Default badge, and the row for an unregistered default, by
/// matching <see cref="AccountViewDto.DirectoryKey"/> against <see cref="DefaultDirectoryDto.DirectoryKey"/>.
/// Only the fields of <see cref="AccountViewDto"/> leave the host: a status line backup, retired
/// sessions and reading sessions never do.</summary>
public static class AccountSnapshotBuilder {
    public static AccountsSnapshotDto Build(
        AccountRegistry                          registry,
        string                                   hostId,
        IReadOnlyDictionary<HarnessId, string>   defaults,
        UserHome                                 home,
        IReadOnlyDictionary<string, ReadingFile> readings
    ) {
        var accounts = new List<AccountViewDto>();

        foreach (var account in registry.Accounts) {
            if (!TryKey(account.Directory, out var key)) continue;

            var state   = AccountWiring.State(account, home);
            var reading = readings.TryGetValue(account.Id, out var file) && file.Reading.Generation == account.Generation ? file.Reading : null;

            accounts.Add(new(
                account.Id, account.Vendor.VendorId, account.Directory, key, account.Label, RecordingStates.Token(state),
                account.Email, account.OrgId, account.OrgName, account.Plan, account.VendorAccountId, account.SignIn,
                account.Generation, account.RefreshedAt, account.RefreshError, account.LimitsError, reading));
        }

        var directories = defaults
            .OrderBy(d => d.Key)
            .Select(d => new DefaultDirectoryDto(d.Key.VendorId, d.Value, TryKey(d.Value, out var key) ? key : d.Value))
            .ToArray();

        return new(hostId, registry.Revision, [.. accounts], directories);
    }

    /// <summary>Equal by value: <see cref="AccountViewDto.Reading"/> is compared with <see cref="ReadingMerge.Equivalent"/>.</summary>
    public static bool Equivalent(AccountsSnapshotDto a, AccountsSnapshotDto b) =>
        a.HostId == b.HostId
     && a.Revision == b.Revision
     && a.Defaults.SequenceEqual(b.Defaults)
     && a.Accounts.Length == b.Accounts.Length
     && a.Accounts.Zip(b.Accounts).All(p => Equivalent(p.First, p.Second));

    static bool Equivalent(AccountViewDto a, AccountViewDto b) =>
        a with { Reading = null } == b with { Reading = null }
     && (a.Reading, b.Reading) switch {
            (null, null)   => true,
            ({ } x, { } y) => ReadingMerge.Equivalent(x, y),
            _              => false
        };

    // An entry no key can be derived for cannot be matched by any reader, so one bad entry drops
    // out of the snapshot instead of failing it; a default still ships, keyed by its raw spelling.
    static bool TryKey(string directory, out string key) {
        try {
            key = DirectoryKey.Of(directory);
            return true;
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            key = "";
            return false;
        }
    }
}

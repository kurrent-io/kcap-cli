using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>Builds what the daemon publishes of a host's accounts. There is no row for a default
/// directory: a reader derives the Default badge, and the row for an unregistered default, by
/// matching <see cref="AccountViewDto.DirectoryKey"/> against <see cref="DefaultDirectoryDto.DirectoryKey"/>.</summary>
public static class AccountSnapshotBuilder {
    public static AccountsSnapshotDto Build(AccountRegistry registry, string hostId, IReadOnlyDictionary<HarnessId, string> defaults, UserHome home) {
        var accounts = new List<AccountViewDto>();

        foreach (var account in registry.Accounts) {
            if (!TryKey(account.Directory, out var key)) continue;

            var state = AccountWiring.State(account.Vendor, account.Directory, home);
            accounts.Add(new(account.Id, account.Vendor.VendorId, account.Directory, key, account.Label, RecordingStates.Token(state)));
        }

        var directories = defaults
            .OrderBy(d => d.Key)
            .Select(d => new DefaultDirectoryDto(d.Key.VendorId, d.Value, TryKey(d.Value, out var key) ? key : d.Value))
            .ToArray();

        return new(hostId, registry.Revision, [.. accounts], directories);
    }

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

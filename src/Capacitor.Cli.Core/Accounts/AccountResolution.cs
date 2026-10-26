using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>Turns a requested account into the directory a launch runs under. An explicit id is
/// honoured or refused, never replaced by the default directory.</summary>
public static class AccountResolution {
    public const string Unavailable = "account_unavailable";

    const int MinIdPrefix = 4;

    /// <summary>A null registry (unreadable) resolves like an empty one.</summary>
    public static AccountResolutionOutcome Resolve(AccountRegistry? registry, HarnessId vendor, string? accountId, string defaultDirectory) {
        if (string.IsNullOrEmpty(accountId)) return Accepted(defaultDirectory, ForDirectory(registry, vendor, defaultDirectory));

        if (ForId(registry, vendor, accountId) is not { } entry) return NoAccount(vendor, accountId);

        return Directory.Exists(entry.Directory) ? Accepted(entry.Directory, entry) : Missing(entry.Directory);
    }

    // A resumed session lives where it started, so the stored directory wins over the entry's
    // current one; the registry only says whether the account may still be used.
    public static AccountResolutionOutcome ResolveForResume(
            AccountRegistry? registry, HarnessId vendor, string? accountId, string? directory, string defaultDirectory
        ) {
        if (string.IsNullOrEmpty(directory)) return Resolve(registry, vendor, accountId, defaultDirectory);

        if (string.IsNullOrEmpty(accountId)) {
            return Directory.Exists(directory) ? Accepted(directory, ForDirectory(registry, vendor, directory)) : Missing(directory);
        }

        if (ForId(registry, vendor, accountId) is not { } entry) return NoAccount(vendor, accountId);

        return Directory.Exists(directory) ? Accepted(directory, entry) : Missing(directory);
    }

    /// <summary>The CLI's <c>--account</c> value: an id prefix, else a directory. The caller expands <c>~</c>.</summary>
    public static AccountResolutionOutcome ResolveArgument(AccountRegistry registry, HarnessId vendor, string idPrefixOrDirectory) {
        var ofVendor = registry.Accounts.Where(a => a.Vendor == vendor).ToList();

        if (idPrefixOrDirectory.Length >= MinIdPrefix) {
            var byId = ofVendor.Where(a => a.Id.StartsWith(idPrefixOrDirectory, StringComparison.OrdinalIgnoreCase)).ToList();

            if (byId.Count == 1) return Accepted(byId[0].Directory, byId[0]);

            if (byId.Count > 1) {
                return Refused($"{Unavailable}: {idPrefixOrDirectory} matches more than one {vendor} account: {string.Join(", ", byId.Select(a => a.Id))}");
            }
        }

        return ofVendor.FirstOrDefault(a => SameDirectory(a.Directory, idPrefixOrDirectory)) is { } byDirectory
            ? Accepted(byDirectory.Directory, byDirectory)
            : Refused($"{Unavailable}: {idPrefixOrDirectory} is not a registered {vendor} account; run kcap accounts add");
    }

    static VendorAccount? ForId(AccountRegistry? registry, HarnessId vendor, string accountId) =>
        registry?.Accounts.FirstOrDefault(a => a.Vendor == vendor && a.Id == accountId);

    static VendorAccount? ForDirectory(AccountRegistry? registry, HarnessId vendor, string directory) {
        var key = DirectoryKey.Of(directory);

        return registry?.Accounts.FirstOrDefault(a => a.Vendor == vendor && KeyOrNull(a.Directory) == key);
    }

    // A hand-edited registry entry may hold anything; it must not throw out of a launch's resolution.
    static string? KeyOrNull(string directory) {
        try {
            return DirectoryKey.Of(directory);
        } catch (Exception ex) when (IsNotAPath(ex)) {
            return null;
        }
    }

    static bool SameDirectory(string registered, string argument) {
        try {
            return AccountDirectory.Same(registered, argument);
        } catch (Exception ex) when (IsNotAPath(ex)) {
            return false;
        }
    }

    static bool IsNotAPath(Exception ex) => ex is ArgumentException or NotSupportedException or IOException;

    // The physical directory: a default reached through a symlink is recorded as the directory behind it,
    // so retargeting the link before a resume cannot move the continuation to another account.
    static AccountResolutionOutcome Accepted(string directory, VendorAccount? entry) {
        var physical = Physical(directory);

        return new(new ResolvedAccount(physical, entry?.Id, entry?.Label ?? Path.GetFileName(physical), entry?.Generation ?? 0), null);
    }

    static string Physical(string directory) {
        try {
            return AccountDirectory.Normalize(directory);
        } catch (Exception ex) when (IsNotAPath(ex)) {
            return Path.TrimEndingDirectorySeparator(directory);
        }
    }

    static AccountResolutionOutcome NoAccount(HarnessId vendor, string accountId) => Refused($"{Unavailable}: no {vendor} account {accountId}");

    static AccountResolutionOutcome Missing(string directory) => Refused($"{Unavailable}: {directory} is missing");

    static AccountResolutionOutcome Refused(string refusal) => new(null, refusal);
}

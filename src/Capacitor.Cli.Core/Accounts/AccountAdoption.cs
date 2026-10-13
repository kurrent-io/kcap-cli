using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Accounts;

public static class AccountAdoption {
    public static IReadOnlyList<VendorAccount> Of(AccountStore store, HarnessId vendor) =>
        [.. store.Load().Accounts.Where(a => a.Vendor == vendor)];

    public static VendorAccount EnsureDefault(AccountStore store, HarnessId vendor, string environmentDirectory, TimeProvider time) =>
        store.Mutate(registry => {
            var existing = registry.Accounts.FirstOrDefault(a => a.Vendor == vendor && AccountDirectory.Same(a.Directory, environmentDirectory));
            if (existing is not null) return (registry, existing);

            var directory = AccountDirectory.Normalize(environmentDirectory);
            var added     = new VendorAccount(Guid.NewGuid().ToString("N"), vendor, directory, Path.GetFileName(directory), time.GetUtcNow());
            return (registry with { Accounts = [.. registry.Accounts, added] }, added);
        });
}

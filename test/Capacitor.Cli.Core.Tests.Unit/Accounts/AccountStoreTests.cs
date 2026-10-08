using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    AccountStore Store() => new(Tmp.PathTo("accounts"));

    static VendorAccount Claude(string dir) =>
        new(Guid.NewGuid().ToString("N"), HarnessId.Claude, dir, Path.GetFileName(dir), DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Load_of_a_missing_store_is_empty() {
        var registry = Store().Load();

        await Assert.That(registry.Accounts.Count).IsEqualTo(0);
        await Assert.That(registry.Revision).IsEqualTo(0L);
    }

    [Test]
    public async Task Mutate_persists_and_increments_the_revision() {
        var store = Store();

        store.Mutate(r => (r with { Accounts = [.. r.Accounts, Claude("/h/.claude-work")] }, 0));

        var loaded = store.Load();
        await Assert.That(loaded.Accounts.Count).IsEqualTo(1);
        await Assert.That(loaded.Revision).IsEqualTo(1L);
    }

    [Test]
    public async Task Mutate_without_a_change_keeps_the_revision() {
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude("/h/.claude")] }, 0));

        store.Mutate(r => (r, 0));

        await Assert.That(store.Load().Revision).IsEqualTo(1L);
    }

    [Test]
    public async Task Concurrent_mutations_lose_no_account() {
        var store = Store();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
            store.Mutate(r => (r with { Accounts = [.. r.Accounts, Claude($"/h/.claude-{i}")] }, 0)))));

        var loaded = store.Load();
        await Assert.That(loaded.Accounts.Count).IsEqualTo(20);
        await Assert.That(loaded.Revision).IsEqualTo(20L);
    }

    [Test]
    public async Task Store_files_are_owner_only() {
        if (OperatingSystem.IsWindows()) return;
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude("/h/.claude")] }, 0));

        await Assert.That(File.GetUnixFileMode(store.Directory) & (UnixFileMode)0b111_111)
            .IsEqualTo((UnixFileMode)0);
        await Assert.That(File.GetUnixFileMode(Path.Combine(store.Directory, "accounts.json")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task HostId_is_stable() {
        var store = Store();

        await Assert.That(store.HostId()).IsEqualTo(store.HostId());
        await Assert.That(new AccountStore(store.Directory).HostId()).IsEqualTo(store.HostId());
    }

    [Test]
    public async Task Beside_places_the_store_next_to_the_daemons_directory() {
        var daemons = new DaemonStore(Tmp.PathTo("cfg", "daemons"));

        await Assert.That(AccountStore.Beside(daemons).Directory).IsEqualTo(Tmp.PathTo("cfg", "accounts"));
    }

    [Test]
    public async Task Normalize_ignores_a_trailing_separator() {
        var dir = Tmp.CreateDir(".claude-work");

        await Assert.That(AccountDirectory.Normalize(dir + Path.DirectorySeparatorChar))
            .IsEqualTo(AccountDirectory.Normalize(dir));
    }

    [Test]
    public async Task Find_matches_a_differently_spelled_directory() {
        var dir   = Tmp.CreateDir(".claude-work");
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude(AccountDirectory.Normalize(dir))] }, 0));

        await Assert.That(store.Find(HarnessId.Claude, dir + Path.DirectorySeparatorChar)).IsNotNull();
        await Assert.That(store.Find(HarnessId.Codex, dir)).IsNull();
    }
}

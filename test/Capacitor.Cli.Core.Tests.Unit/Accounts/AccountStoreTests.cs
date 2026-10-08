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

        store.HostId();
        await Assert.That(File.GetUnixFileMode(Path.Combine(store.Directory, "host.json")))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task A_looser_existing_directory_is_tightened() {
        if (OperatingSystem.IsWindows()) return;
        var dir = Tmp.CreateDir("accounts");
        File.SetUnixFileMode(dir, (UnixFileMode)0b111_111_101);
        var store = new AccountStore(dir);

        store.Mutate(r => (r with { Accounts = [Claude("/h/.claude")] }, 0));

        await Assert.That(File.GetUnixFileMode(dir)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task HostId_refuses_a_file_without_a_host_id() {
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        await File.WriteAllTextAsync(Path.Combine(store.Directory, "host.json"), "{}");

        await Assert.That(store.HostId).Throws<InvalidDataException>();
    }

    [Test]
    public async Task HostId_refuses_a_corrupt_file() {
        var store = Store();
        Directory.CreateDirectory(store.Directory);
        await File.WriteAllTextAsync(Path.Combine(store.Directory, "host.json"), "not json");

        await Assert.That(store.HostId).Throws<InvalidDataException>();
    }

    [Test]
    public async Task The_registry_is_written_with_snake_case_keys_and_a_string_vendor() {
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude("/h/.claude")] }, 0));

        var json = await File.ReadAllTextAsync(Path.Combine(store.Directory, "accounts.json"));

        await Assert.That(json).Contains("\"vendor\": \"Claude\"");
        await Assert.That(json).Contains("\"added_at\"");
        await Assert.That(json).Contains("\"revision\"");
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

    [Test]
    public async Task TryLoad_returns_the_registry() {
        var store = Store();
        store.Mutate(r => (r with { Accounts = [Claude("/h/.claude")] }, 0));

        await Assert.That(store.TryLoad()!.Accounts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task TryLoad_of_a_corrupt_file_is_null() {
        Tmp.CreateFile("accounts/accounts.json", "{not json");

        await Assert.That(Store().TryLoad()).IsNull();
    }

    const string HandEditedRegistry = """
        { "version": 1, "revision": 3, "accounts": [
            null,
            { "id": "", "vendor": "Claude", "directory": "/h/.claude-empty-id", "label": "x", "added_at": "2026-01-01T00:00:00Z" },
            { "vendor": "Claude", "directory": "/h/.claude-no-id", "label": "x", "added_at": "2026-01-01T00:00:00Z" },
            { "id": "nodir", "vendor": "Codex", "label": "x", "added_at": "2026-01-01T00:00:00Z" },
            { "id": "blank", "vendor": "Codex", "directory": " ", "label": "x", "added_at": "2026-01-01T00:00:00Z" },
            { "id": "relative", "vendor": "Claude", "directory": "relative/.claude", "label": "x", "added_at": "2026-01-01T00:00:00Z" },
            { "id": "ok", "vendor": "Claude", "directory": "/h/.claude-work", "label": "work", "added_at": "2026-01-01T00:00:00Z" }
        ] }
        """;

    [Test]
    public async Task Load_drops_hand_edited_entries_without_an_id_or_a_rooted_directory() {
        Tmp.CreateFile("accounts/accounts.json", HandEditedRegistry);

        var registry = Store().Load();

        await Assert.That(registry.Accounts.Select(a => a.Id)).IsEquivalentTo(["ok"]);
        await Assert.That(registry.Revision).IsEqualTo(3L);
    }

    [Test]
    public async Task Hook_path_lookups_do_not_throw_on_a_hand_edited_registry() {
        Tmp.CreateFile("accounts/accounts.json", HandEditedRegistry);
        var registry = Store().TryLoad()!;
        var home     = new UserHome(Tmp.PathTo("home"));

        await Assert.That(AccountPaths.ClaudeForTranscript(Tmp.PathTo("t.jsonl"), registry, home)).IsNull();
        await Assert.That(AccountPaths.CodexForRollout(Tmp.PathTo("r.jsonl"), registry, home)).IsNull();
    }
}

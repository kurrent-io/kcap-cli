using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountSnapshotBuilderTests {
    [TempHome] public required TempHome Home { get; init; }

    static VendorAccount Account(string id, HarnessId vendor, string directory, string label) =>
        new(id, vendor, AccountDirectory.Normalize(directory), label, DateTimeOffset.UnixEpoch);

    static AccountRegistry Registry(long revision, params VendorAccount[] accounts) => new() { Revision = revision, Accounts = accounts };

    string WiredClaude() {
        var dir = Home.CreateDir(".claude-work");
        Home.CreateFile(".claude-work/settings.json", """{ "enabledPlugins": { "kcap@kcap": true } }""");
        return dir;
    }

    [Test]
    public async Task Builds_one_view_per_account_with_key_label_and_recording_state() {
        var claude = WiredClaude();
        var codex  = Home.CreateDir(".codex-b");
        var registry = Registry(7,
            Account("c1", HarnessId.Claude, claude, "Work"),
            Account("x1", HarnessId.Codex, codex, "Spare"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1", new Dictionary<HarnessId, string>(), Home);

        await Assert.That(snapshot.HostId).IsEqualTo("host-1");
        await Assert.That(snapshot.Revision).IsEqualTo(7L);
        await Assert.That(snapshot.Accounts.Length).IsEqualTo(2);
        await Assert.That(snapshot.Accounts[0]).IsEqualTo(
            new AccountViewDto("c1", "claude", AccountDirectory.Normalize(claude), DirectoryKey.Of(claude), "Work", "installed"));
        await Assert.That(snapshot.Accounts[1]).IsEqualTo(
            new AccountViewDto("x1", "codex", AccountDirectory.Normalize(codex), DirectoryKey.Of(codex), "Spare", "not_wired"));
    }

    [Test]
    public async Task Defaults_carry_directory_and_key_only() {
        var claude = WiredClaude();
        var codex  = Home.PathTo(".codex");
        var registry = Registry(1, Account("c1", HarnessId.Claude, claude, "Work"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Codex] = codex, [HarnessId.Claude] = claude }, Home);

        await Assert.That(snapshot.Defaults.Length).IsEqualTo(2);
        await Assert.That(snapshot.Defaults[0]).IsEqualTo(new DefaultDirectoryDto("claude", claude, DirectoryKey.Of(claude)));
        await Assert.That(snapshot.Defaults[1]).IsEqualTo(new DefaultDirectoryDto("codex", codex, DirectoryKey.Of(codex)));
        await Assert.That(snapshot.Defaults[0].DirectoryKey).IsEqualTo(snapshot.Accounts[0].DirectoryKey);
    }

    [Test]
    public async Task Unregistered_default_matches_no_view_by_key() {
        var claude  = WiredClaude();
        var codex   = Home.CreateDir(".codex-b");
        var registry = Registry(1, Account("c1", HarnessId.Claude, claude, "Work"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Codex] = codex }, Home);

        await Assert.That(snapshot.Accounts.Any(a => a.DirectoryKey == snapshot.Defaults[0].DirectoryKey)).IsFalse();
    }

    [Test]
    public async Task Keys_equal_across_casing_off_linux() {
        var work     = Home.CreateDir("Work");
        var registry = Registry(1, Account("c1", HarnessId.Claude, work, "Work"));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Claude] = Home.PathTo("work") }, Home);

        if (OperatingSystem.IsLinux())
            await Assert.That(snapshot.Defaults[0].DirectoryKey).IsNotEqualTo(snapshot.Accounts[0].DirectoryKey);
        else
            await Assert.That(snapshot.Defaults[0].DirectoryKey).IsEqualTo(snapshot.Accounts[0].DirectoryKey);
    }

    [Test]
    public async Task A_registry_entry_whose_directory_cannot_be_keyed_is_skipped() {
        var claude   = WiredClaude();
        var registry = Registry(1,
            Account("c1", HarnessId.Claude, claude, "Work"),
            new VendorAccount("bad", HarnessId.Codex, "/nul\0dir", "Bad", DateTimeOffset.UnixEpoch));

        var snapshot = AccountSnapshotBuilder.Build(registry, "host-1", new Dictionary<HarnessId, string>(), Home);

        await Assert.That(snapshot.Accounts.Select(a => a.Id).ToArray()).IsEquivalentTo(new[] { "c1" });
    }

    [Test]
    public async Task A_default_whose_directory_cannot_be_keyed_carries_the_raw_directory_as_its_key() {
        var snapshot = AccountSnapshotBuilder.Build(Registry(1), "host-1",
            new Dictionary<HarnessId, string> { [HarnessId.Codex] = "/nul\0dir" }, Home);

        await Assert.That(snapshot.Defaults[0]).IsEqualTo(new DefaultDirectoryDto("codex", "/nul\0dir", "/nul\0dir"));
    }
}

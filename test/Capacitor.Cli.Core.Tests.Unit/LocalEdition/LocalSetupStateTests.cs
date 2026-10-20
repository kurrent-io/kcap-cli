using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalSetupStateTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= CreateRoot();

    LocalRoot CreateRoot() {
        var root = new LocalRoot(Tmp.PathTo("local"));
        root.EnsureCreated();
        return root;
    }

    void Journal(string operation, string phase) =>
        LocalStateFiles.WriteJournal(Root, new(operation, phase, ServerStart.Deny, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    [Test]
    public async Task No_journal_is_not_pending() {
        await Assert.That(LocalSetupState.Pending(Root)).IsFalse();
    }

    [Test]
    public async Task A_setup_entry_in_any_phase_is_pending() {
        Journal(LocalSetupState.Operation, "committing");

        await Assert.That(LocalSetupState.Pending(Root)).IsTrue();
    }

    [Test]
    public async Task An_unreadable_journal_is_pending() {
        var journal = Tmp.CreateFile(["local", "journal.json"], "{not json");

        await Assert.That(journal).IsEqualTo(Root.JournalPath);

        await Assert.That(LocalSetupState.Pending(Root)).IsTrue();
    }

    [Test]
    public async Task An_upgrade_entry_is_not_pending_setup() {
        Journal("upgrade", "committing");

        await Assert.That(LocalSetupState.Pending(Root)).IsFalse();
    }

    [Test]
    public async Task An_inactive_cloud_profile_is_foreign() {
        var config = new ProfileConfig {
            ActiveProfile = "",
            Profiles = new() { ["cloud"] = new Profile { ServerUrl = "https://x.kcap.ai" } },
        };

        await Assert.That(LocalSetupState.HasForeignServerProfile(config, LocalEndpoints.None)).IsTrue();
    }

    [Test]
    public async Task Blank_server_profiles_are_not_foreign() {
        var config = new ProfileConfig { Profiles = new() { ["blank"] = new Profile { ServerUrl = "" } } };

        await Assert.That(LocalSetupState.HasForeignServerProfile(config, LocalEndpoints.None)).IsFalse();
    }
}

using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

public class UsageDataEditionTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    LocalRoot Root => field ??= CreateRoot();

    LocalRoot CreateRoot() {
        var root = new LocalRoot(Tmp.PathTo("local"));
        root.EnsureCreated();
        return root;
    }

    void SettingUpLocally() =>
        LocalStateFiles.WriteJournal(Root, new(LocalSetupState.Operation, "installing", ServerStart.Deny, null, DateTimeOffset.UnixEpoch,
            new Dictionary<string, string>()));

    void InstalledLocally() =>
        LocalStateFiles.PublishRegistry(Root, new InstallRegistry(new LocalInstallation("inst-1", 51234, "http://localhost:51234", "1.4.0"), []));

    void CloudProfile() =>
        ConfigMutator.Mutate(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "acme",
            Profiles      = new() { ["acme"] = new Profile { ServerUrl = "https://acme.kcap.ai" } },
        });

    [Test]
    public async Task A_machine_with_nothing_set_up_is_not_local() {
        await Assert.That(UsageDataEdition.IsLocal(Root)).IsFalse();
    }

    [Test]
    public async Task An_installation_or_a_setup_in_flight_is_local() {
        SettingUpLocally();
        await Assert.That(UsageDataEdition.IsLocal(Root)).IsTrue();

        LocalStateFiles.ClearJournal(Root);
        await Assert.That(UsageDataEdition.IsLocal(Root)).IsFalse();

        InstalledLocally();
        await Assert.That(UsageDataEdition.IsLocal(Root)).IsTrue();
    }

    [Test]
    public async Task Local_state_that_cannot_be_read_counts_as_local() {
        Tmp.CreateFile(["local", "install.json"], "{not json");

        await Assert.That(UsageDataEdition.IsLocal(Root)).IsTrue();
    }

    /// <summary>A fresh app waits for the wizard's edition before it sends anything; only on a machine that cannot run
    /// the local edition is the edition already known.</summary>
    [Test]
    public async Task The_desktop_app_waits_on_a_fresh_machine_that_could_go_local() {
        await Assert.That(UsageDataEdition.ForDesktop(Config.Root, Root, localEditionSupported: true)).IsEqualTo(UnsavedConsent.Pending);
        await Assert.That(UsageDataEdition.ForDesktop(Config.Root, Root, localEditionSupported: false)).IsEqualTo(UnsavedConsent.Share);
    }

    [Test]
    public async Task The_desktop_app_keeps_the_cloud_notice_for_a_cloud_workspace_and_waits_in_the_local_edition() {
        CloudProfile();
        await Assert.That(UsageDataEdition.ForDesktop(Config.Root, Root, localEditionSupported: true)).IsEqualTo(UnsavedConsent.Share);

        InstalledLocally();
        await Assert.That(UsageDataEdition.ForDesktop(Config.Root, Root, localEditionSupported: false)).IsEqualTo(UnsavedConsent.Pending);
    }
}

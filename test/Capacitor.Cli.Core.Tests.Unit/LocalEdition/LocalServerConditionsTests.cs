using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>Each state an uninstall, a purge or a running command leaves on disk reads as its own condition, and only an
/// installation with its files in place reads as one a start can bring up.</summary>
public class LocalServerConditionsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= new(Tmp.PathTo("local"));

    static readonly LocalInstallation Active = new("inst-1", 51234, "http://localhost:51234", "1.4.0");

    async Task PublishAsync(InstallRegistry registry) {
        using var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
        lease.PublishRegistry(registry);
    }

    void CreateBinary() {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalServerConditions.ServerBinary(Root))!);
        File.WriteAllText(LocalServerConditions.ServerBinary(Root), "");
    }

    [Test]
    public async Task An_empty_root_is_not_installed() {
        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.NotInstalled);
    }

    [Test]
    public async Task An_installation_with_its_binary_is_installed() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        CreateBinary();

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Installed);
    }

    [Test]
    public async Task A_registry_left_without_bin_is_uninstalled() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Uninstalled);
    }

    [Test]
    public async Task A_bin_without_the_server_binary_is_missing_files() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        Directory.CreateDirectory(Path.Combine(Root.Directory, "bin"));

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.FilesMissing);
    }

    [Test]
    public async Task A_retired_installation_with_none_active_is_purged() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active).RetireActive());

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Purged);
    }

    [Test]
    public async Task A_held_lifecycle_lock_is_changing_whatever_the_registry_says() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        CreateBinary();

        using (await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Changing);

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Installed);
    }

    [Test]
    public async Task A_journal_left_behind_is_interrupted() {
        await PublishAsync(InstallRegistry.Empty.WithActive(Active));
        CreateBinary();
        using (var lease = await LifecycleLease.AcquireAsync(Root, TimeSpan.Zero, TimeProvider.System, CancellationToken.None))
            lease.Begin("upgrade", "staging", ServerStart.Allow);

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Interrupted);
    }

    [Test]
    public async Task An_unreadable_registry_is_unreadable_not_absent() {
        Root.EnsureCreated();
        File.WriteAllText(Root.RegistryPath, "{not json");

        await Assert.That(LocalServerConditions.Read(Root)).IsEqualTo(LocalServerCondition.Unreadable);
    }

    [Test]
    public async Task Only_an_installed_server_has_no_explanation() {
        foreach (var condition in Enum.GetValues<LocalServerCondition>())
            await Assert.That(condition.Explanation is null).IsEqualTo(condition == LocalServerCondition.Installed);
    }
}

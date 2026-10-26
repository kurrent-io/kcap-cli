using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class ExperimentalSettingsViewModelTests {
    const string Id = "codex-app-server";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static FakeDaemonClientService Connected() {
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        return service;
    }

    static DaemonStatusDto Reporting(
            bool resolved, bool active, bool acknowledged = true, string? reason = null, string connection = "connected") =>
        FakeDaemonClientService.Snap(connection: connection) with {
            Experimental = [new ExperimentalFeatureStatusDto(Id, resolved, active, 1, acknowledged, reason)],
        };

    [Test]
    public Task Toggling_saves_to_the_attached_profile_only() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        var row = vm.Features.Single();

        row.IsEnabled = true;

        await Assert.That(store.Load("work").Features.GetValueOrDefault(Id)).IsTrue();
        await Assert.That(store.Load("work").IsEnabled(Id)).IsTrue();
        await Assert.That(store.Load("another").IsEnabled(Id)).IsFalse();
        using var reopened = new ExperimentalSettingsViewModel(store, "work", Connected());
        await Assert.That(reopened.Features.Single().IsEnabled).IsTrue();
    });

    [Test]
    public Task Status_text_follows_each_snapshot() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        var service = Connected();
        using var vm = new ExperimentalSettingsViewModel(store, "work", service);
        var row = vm.Features.Single();
        await Assert.That(row.StatusText).IsEqualTo("");

        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        await Assert.That(row.StatusText).IsEqualTo("This daemon is too old to apply it. Update kcap and restart the daemon");
        await Assert.That(row.StatusIsWarning).IsTrue();

        service.SnapshotsSubject.OnNext(Reporting(resolved: false, active: false));
        await Assert.That(row.StatusText).IsEqualTo("");

        row.IsEnabled = true;
        await Assert.That(row.StatusText).IsEqualTo("Applying…");
        await Assert.That(row.StatusIsWarning).IsFalse();

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true));
        await Assert.That(row.StatusText).IsEqualTo("Active");

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true, acknowledged: false));
        await Assert.That(row.StatusText).IsEqualTo("Applying…");

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true, acknowledged: false, connection: "reconnecting"));
        await Assert.That(row.StatusText).IsEqualTo("Applied; the server has not been told yet");

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: false, reason: "codex is older than 0.146.0"));
        await Assert.That(row.StatusText).IsEqualTo("On, not active: codex is older than 0.146.0");
        await Assert.That(row.StatusIsWarning).IsTrue();

        service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, null, null));
        await Assert.That(row.StatusText).IsEqualTo("Saved; applies when the daemon starts");
        await Assert.That(row.StatusIsWarning).IsFalse();
    });

    [Test]
    public Task Corrupt_store_disables_the_toggles_with_the_problem() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        await File.WriteAllTextAsync(store.Path, "{ not json");
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        var row = vm.Features.Single();

        await Assert.That(row.CanChange).IsFalse();
        await Assert.That(row.IsEnabled).IsFalse();
        await Assert.That(vm.Problem).Contains("not valid JSON");

        row.IsEnabled = true;

        await Assert.That(row.IsEnabled).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(store.Path)).IsEqualTo("{ not json");
    });

    [Test]
    public Task A_save_the_store_refuses_puts_the_toggle_back() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        var row = vm.Features.Single();
        await File.WriteAllTextAsync(store.Path, "[1]");

        row.IsEnabled = true;

        await Assert.That(row.IsEnabled).IsFalse();
        await Assert.That(vm.Problem).Contains("not a JSON object");
    });
}

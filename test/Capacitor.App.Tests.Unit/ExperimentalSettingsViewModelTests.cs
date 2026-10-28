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

    static Task Until(Func<bool> condition, string what) => WorkspaceFixtures.WaitUntilAsync(condition, what: what);

    static DaemonStatusDto Reporting(
            bool resolved, bool active, bool acknowledged = true, string? reason = null, bool failed = false) =>
        FakeDaemonClientService.Snap() with {
            Experimental = [new ExperimentalFeatureStatusDto(Id, resolved, active, 1, acknowledged, reason, failed)],
        };

    [Test]
    public Task Toggling_saves_to_the_attached_profile_only() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        await vm.Loaded;
        var row = vm.Features.Single();

        row.IsEnabled = true;

        await WorkspaceFixtures.WaitUntilAsync(() => store.Load("work").IsEnabled(Id), what: "the save");
        await Assert.That(store.Load("another").IsEnabled(Id)).IsFalse();
        using var reopened = new ExperimentalSettingsViewModel(store, "work", Connected());
        await reopened.Loaded;
        await Assert.That(reopened.Features.Single().IsEnabled).IsTrue();
    });

    /// <summary>The store is read off the UI thread, so the constructor cannot know the saved value: the
    /// switch stays locked until that read is applied back on the UI thread.</summary>
    [Test]
    public Task The_switches_unlock_with_the_first_read() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        await store.SetAsync("work", Id, true, CancellationToken.None);
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        var row = vm.Features.Single();

        await Assert.That(row.CanChange).IsFalse();
        await Assert.That(row.IsEnabled).IsFalse();

        await vm.Loaded;

        await Assert.That(row.CanChange).IsTrue();
        await Assert.That(row.IsEnabled).IsTrue();
    });

    [Test]
    [Arguments(false, "")]
    [Arguments(true, ExperimentalFeatureRowViewModel.WaitingForServerText)]
    public Task An_unregistered_daemon_reads_as_no_change_until_the_switch_is_flipped(bool on, string expected) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var store = new ExperimentalSettingsStore(Config.Root);
            if (on) await store.SetAsync("work", Id, true, CancellationToken.None);
            var service = Connected();
            using var vm = new ExperimentalSettingsViewModel(store, "work", service);
            await vm.Loaded;
            var row = vm.Features.Single();

            service.SnapshotsSubject.OnNext(Reporting(resolved: on, active: on, acknowledged: false, failed: true));
            await Until(() => row.StatusText == expected, "an unprompted registration failure");

            row.IsEnabled = !on;
            service.SnapshotsSubject.OnNext(Reporting(resolved: !on, active: !on, acknowledged: false, failed: true));
            await Until(() => row.StatusText == ExperimentalFeatureRowViewModel.AppliedLocallyText, "a flipped switch");

            // The CLI sets it back; each snapshot rereads the store, as the page does while it is open.
            await Until(() => store.Load("work").IsEnabled(Id) == !on, "the flip saved");
            await store.SetAsync("work", Id, on, CancellationToken.None);
            await Until(() => {
                service.SnapshotsSubject.OnNext(Reporting(resolved: on, active: on, acknowledged: false, failed: true));
                return row.IsEnabled == on && row.StatusText == expected;
            }, "a value set elsewhere");
        });

    [Test]
    public Task Status_text_follows_each_snapshot() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        var service = Connected();
        using var vm = new ExperimentalSettingsViewModel(store, "work", service);
        await vm.Loaded;
        var row = vm.Features.Single();
        await Assert.That(row.StatusText).IsEqualTo("");

        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        await Until(() => row.StatusText == "The daemon runs an older version of kcap. Update kcap. The daemon then restarts on its own.", "a pre-feature daemon");
        await Assert.That(row.StatusIsWarning).IsTrue();

        service.SnapshotsSubject.OnNext(Reporting(resolved: false, active: false));
        await Until(() => row.StatusText == "", "an off switch");

        row.IsEnabled = true;
        await Assert.That(row.StatusText).IsEqualTo("Applying…");
        await Assert.That(row.StatusIsWarning).IsFalse();

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true));
        await Until(() => row.StatusText == "Active", "active");

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true, acknowledged: false));
        await Until(() => row.StatusText == "Applying…", "applying");

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true, acknowledged: false, failed: true));
        await Until(() => row.StatusText == ExperimentalFeatureRowViewModel.AppliedLocallyText, "applied locally");

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: false, reason: "codex is older than 0.146.0"));
        await Until(() => row.StatusText == "On, not active: codex is older than 0.146.0", "not active");
        await Assert.That(row.StatusIsWarning).IsTrue();

        service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, null, null));
        await Assert.That(row.StatusText).IsEqualTo("Saved. Applies when the daemon starts.");
        await Assert.That(row.StatusIsWarning).IsFalse();
    });

    /// <summary>Snapshots can arrive on the UI thread. The store is read off it, so nothing the snapshot
    /// carries can have been applied by the time the emit returns there.</summary>
    [Test]
    public Task A_snapshot_emitted_on_the_ui_thread_reads_the_store_off_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        var service = Connected();
        using var vm = new ExperimentalSettingsViewModel(store, "work", service);
        await vm.Loaded;
        var row = vm.Features.Single();
        await store.SetAsync("work", Id, true, CancellationToken.None);

        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true));

        await Assert.That(row.IsEnabled).IsFalse();
        await Until(() => row.IsEnabled && row.StatusText == "Active", "the snapshot's read");
    });

    [Test]
    public Task An_off_switch_on_a_stopped_daemon_shows_no_status() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = Connected();
        using var vm = new ExperimentalSettingsViewModel(new ExperimentalSettingsStore(Config.Root), "work", service);
        await vm.Loaded;
        var row = vm.Features.Single();

        service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, null, null));

        await Assert.That(row.StatusText).IsEqualTo("");
    });

    /// <summary>A flip made by the CLI while the tab is open reaches the daemon too; reading the switch only
    /// at open would leave the row comparing a stale value with every snapshot after it.</summary>
    [Test]
    public Task Each_snapshot_rereads_the_store() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        var service = Connected();
        using var vm = new ExperimentalSettingsViewModel(store, "work", service);
        await vm.Loaded;
        var row = vm.Features.Single();
        service.SnapshotsSubject.OnNext(Reporting(resolved: false, active: false));

        await store.SetAsync("work", Id, true, CancellationToken.None);
        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true));

        await Until(() => row.IsEnabled && row.StatusText == "Active", "the CLI's flip");

        await File.WriteAllTextAsync(store.Path, "{ not json");
        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true));

        await Until(() => !row.CanChange, "the corrupt store");
        await Assert.That(vm.Problem).Contains("not valid JSON");

        File.Delete(store.Path);
        service.SnapshotsSubject.OnNext(Reporting(resolved: false, active: false));

        await Until(() => row.CanChange && vm.Problem is null, "the removed store");
        await Assert.That(row.IsEnabled).IsFalse();
    });

    /// <summary>The file is re-read only when its last-write time or length moved: here the content
    /// changes under an unchanged fingerprint, which the row must not see until the time moves.</summary>
    [Test]
    public Task A_snapshot_rereads_only_a_store_whose_fingerprint_moved() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        await store.SetAsync("work", Id, false, CancellationToken.None);
        var service = Connected();
        using var vm = new ExperimentalSettingsViewModel(store, "work", service);
        await vm.Loaded;
        var row = vm.Features.Single();
        var written = File.GetLastWriteTimeUtc(store.Path);
        var before = await File.ReadAllTextAsync(store.Path);

        await File.WriteAllTextAsync(store.Path, before.Replace("false", "true "));
        File.SetLastWriteTimeUtc(store.Path, written);
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());

        await Until(() => row.StatusText != "", "the first snapshot");
        await Assert.That(row.IsEnabled).IsFalse();

        File.SetLastWriteTimeUtc(store.Path, written.AddSeconds(1));
        service.SnapshotsSubject.OnNext(Reporting(resolved: true, active: true));

        await Until(() => row.IsEnabled, "the moved fingerprint");
    });

    /// <summary>A snapshot's read can start before a toggle is saved and arrive after it; applying it would
    /// flip the row back to the old value.</summary>
    [Test]
    public Task A_read_that_predates_a_save_does_not_flip_the_row_back() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        await vm.Loaded;
        var row = vm.Features.Single();
        await store.SetAsync("work", Id, false, CancellationToken.None);
        var stale = vm.ReadIfChanged();
        await Assert.That(stale).IsNotNull();

        row.IsEnabled = true;
        vm.Apply(stale!.Value);

        await Assert.That(row.IsEnabled).IsTrue();
        await WorkspaceFixtures.WaitUntilAsync(() => store.Load("work").IsEnabled(Id), what: "the save");
    });

    /// <summary>Reads run off the UI thread and can land in either order; a read taken before a newer one
    /// that already applied must not put the older value back, since the fingerprint now matches the file
    /// and no later snapshot would correct it.</summary>
    [Test]
    public Task A_read_that_lands_after_a_newer_one_is_dropped() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        await vm.Loaded;
        var row = vm.Features.Single();
        await store.SetAsync("work", Id, true, CancellationToken.None);
        var older = vm.ReadIfChanged()!.Value;
        await store.SetAsync("work", Id, false, CancellationToken.None);
        var newer = vm.ReadIfChanged()!.Value;

        vm.Apply(newer);
        vm.Apply(older);

        await Assert.That(row.IsEnabled).IsFalse();
        await Assert.That(vm.ReadIfChanged()).IsNull();
    });

    [Test]
    public Task Corrupt_store_disables_the_toggles_with_the_problem() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new ExperimentalSettingsStore(Config.Root);
        await File.WriteAllTextAsync(store.Path, "{ not json");
        using var vm = new ExperimentalSettingsViewModel(store, "work", Connected());
        await vm.Loaded;
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
        await vm.Loaded;
        var row = vm.Features.Single();
        await File.WriteAllTextAsync(store.Path, "[1]");

        row.IsEnabled = true;

        await WorkspaceFixtures.WaitUntilAsync(() => vm.Problem is not null, what: "the refused save's reload");
        await Assert.That(row.IsEnabled).IsFalse();
        await Assert.That(vm.Problem).Contains("not a JSON object");
    });
}

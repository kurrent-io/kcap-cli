using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SettingsViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SettingsProfileStore Seed() {
        ConfigMutator.Mutate(Config.Root, c => c with {
            ActiveProfile = "another",
            Profiles = new() {
                ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } },
                ["another"] = new Profile { ServerUrl = "https://another.example", Daemon = new DaemonSettings { MaxAgents = 12 } },
            }
        });
        return new SettingsProfileStore(Config.Root, "work", "https://work.example");
    }

    static SettingsViewModel Make(SettingsProfileStore store, FakeDaemonClientService service, ScriptedLocalControlOps? ops = null,
            Func<string, CancellationToken, Task<bool>>? target = null,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>>? run = null,
            Func<LifecyclePrompt, CancellationToken, Task<bool>>? confirm = null,
            Func<CancellationToken, Task<bool>>? relaunch = null, bool mac = true,
            Task? startup = null, bool nameOverride = false, bool needsRestart = false,
            Func<MutationRequest, CancellationToken, Task<bool>>? canRetire = null) =>
        new(store, service, ops ?? new ScriptedLocalControlOps(), target ?? ((_, _) => Task.FromResult(false)),
            run ?? ((_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded())),
            confirm ?? ((_, _) => Task.FromResult(true)), relaunch ?? (_ => Task.FromResult(false)), mac,
            startup ?? Task.CompletedTask, canRetire ?? ((_, _) => Task.FromResult(true)), nameOverride, needsRestart);

    static FakeDaemonClientService Connected(int active = 0, bool supportsSettings = true) {
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, supportsSettings ? [SettingsWire.Capability] : []));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: active));
        return service;
    }

    [Test]
    public Task Rename_waits_for_startup_even_when_unreachable() => AvaloniaSession.RunOnUiAsync(async () => {
        var startup = new TaskCompletionSource();
        var service = Connected();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        using var vm = Make(Seed(), service, startup: startup.Task);
        vm.Name = "renamed";
        vm.Capacity = 8;
        await Assert.That(vm.CanSave).IsTrue();
        await Assert.That(vm.CanRename).IsFalse();
        await Assert.That(vm.RenameHint!).Contains("startup");
        startup.SetResult();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        await Assert.That(vm.CanRename).IsTrue();
    });

    [Test]
    public Task Environment_name_override_blocks_only_rename() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = Make(Seed(), Connected(), nameOverride: true);
        vm.Name = "renamed";
        vm.Capacity = 8;
        await Assert.That(vm.CanRename).IsFalse();
        await Assert.That(vm.CanSave).IsTrue();
        await Assert.That(vm.RenameHint!).Contains("KCAP_DAEMON_NAME");
    });

    [Test]
    public Task A_case_only_service_id_change_does_not_offer_rename() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = Connected();
        service.DaemonName = "Work-Laptop";
        using var vm = Make(Seed(), service);
        vm.Name = "work-laptop";
        await Assert.That(vm.NameError).IsNull();
        await Assert.That(vm.CanRename).IsFalse();
        await Assert.That(vm.RenameHint!).Contains("already the daemon’s service id");
    });

    [Test]
    public Task Reopening_settings_for_a_retired_graph_requires_restart() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = Make(Seed(), Connected(), needsRestart: true);
        vm.Name = "renamed";
        vm.Capacity = 8;
        await Assert.That(vm.CanRename).IsFalse();
        await Assert.That(vm.CanSave).IsFalse();
        await Assert.That(vm.RenameHint!).Contains("Restart this app");
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task Failed_capability_probe_or_agents_starting_during_it_preserves_the_profile(bool becameBusy) => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        var service = Connected();
        var runs = 0;
        using var vm = Make(store, service, canRetire: (_, _) => {
            if (becameBusy) service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 1));
            return Task.FromResult(becameBusy);
        }, run: (_, _) => { runs++; return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()); });
        vm.Name = "renamed";
        await vm.RenameCommand.Execute();
        await Assert.That(store.Load().Name).IsEqualTo("daemon-a");
        await Assert.That(runs).IsEqualTo(0);
        await Assert.That(vm.Message!).Contains(becameBusy ? "idle" : "saved name is unchanged");
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public Task A_changed_CLI_restores_only_the_name_it_wrote(bool concurrentName) => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = Make(store, Connected(), run: async (_, ct) => {
            await store.SaveCapacityAsync(9, ct);
            if (concurrentName) await store.SaveNameAsync("concurrent", ct);
            return new MutationOutcome.Failed(30, "cli_unsupported", RecoverySurface.Attention);
        });
        vm.Name = "renamed";
        await vm.RenameCommand.Execute();
        await Assert.That(store.Load().Name).IsEqualTo(concurrentName ? "concurrent" : "daemon-a");
        await Assert.That(store.Load().MaxAgents).IsEqualTo(9);
        await Assert.That(vm.Message!).Contains("CLI no longer supports");
    });

    [Test]
    public Task Editing_gates_commands_without_saving() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = Make(store, Connected());
        await Assert.That(vm.CanSave).IsFalse();
        await Assert.That(vm.CanRename).IsFalse();
        vm.Capacity = -1;
        vm.Name = "Bad Name";
        await Assert.That(vm.CanSave).IsFalse();
        await Assert.That(vm.NameError).IsNotNull();
        vm.Capacity = 1.5m;
        await Assert.That(vm.CanSave).IsFalse();
        vm.Capacity = 8;
        vm.Name = "new-name";
        await Assert.That(vm.CanSave).IsTrue();
        await Assert.That(vm.CanRename).IsTrue();
        await Assert.That(store.Load().Name).IsEqualTo("daemon-a");
        await Assert.That(store.Load().MaxAgents).IsEqualTo(5);
    });

    [Test]
    public Task Zero_capacity_is_valid_and_means_unlimited() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        using var vm = Make(store, Connected());
        vm.Capacity = 0;
        await Assert.That(vm.CapacityError).IsNull();
        await Assert.That(vm.CanSave).IsTrue();
    });

    [Test]
    public Task Capacity_is_persisted_before_the_live_ack_and_does_not_save_the_name() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        var ops = new ScriptedLocalControlOps();
        var ack = ops.ArmPutSettings();
        var putStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ops.SettingsPutStarted = () => putStarted.TrySetResult();
        using var vm = Make(store, Connected(active: 4), ops);
        vm.Capacity = 2;
        vm.Name = "unsaved-name";
        var saving = vm.SaveCommand.Execute().ToTask();
        await putStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(ops.PutSettingsCalls).IsEqualTo(1);
        await Assert.That(store.Load().MaxAgents).IsEqualTo(2);
        await Assert.That(store.Load().Name).IsEqualTo("daemon-a");
        await Assert.That(vm.IsBusy).IsTrue();
        await Assert.That(vm.CanRename).IsFalse();
        ack.SetResult(new DaemonSettingsAckDto(true, null, 2));
        await saving;
        await Assert.That(vm.Message).IsEqualTo("Saved and applied to the running daemon.");
        await Assert.That(vm.CanSave).IsFalse();
        var other = ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root)).Profiles["another"];
        await Assert.That(other.Daemon!.MaxAgents).IsEqualTo(12);
    });

    [Test]
    [Arguments("offline", "will apply when the daemon starts")]
    [Arguments("old", "Update the daemon")]
    [Arguments("transport", "could not reach")]
    [Arguments("refusal", "invalid_max_agents")]
    [Arguments("bad-ack", "unexpected reply")]
    public Task Save_reports_degraded_results_honestly(string mode, string message) => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        var service = Connected(supportsSettings: mode != "old");
        if (mode == "offline") service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        var ops = new ScriptedLocalControlOps();
        if (mode == "transport") ops.QueuePutSettingsFailure(DaemonSettingsReasons.Transport);
        else ops.QueuePutSettings(mode == "bad-ack", mode == "refusal" ? "invalid_max_agents" : null, 5);
        using var vm = Make(store, service, ops);
        vm.Capacity = 9;
        await vm.SaveCommand.Execute();
        await Assert.That(store.Load().MaxAgents).IsEqualTo(9);
        await Assert.That(vm.Message!).Contains(message);
        await Assert.That(ops.PutSettingsCalls).IsEqualTo(mode is "offline" or "old" ? 0 : 1);
    });

    [Test]
    public Task Profile_repointing_blocks_persistence_and_live_apply() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        var ops = new ScriptedLocalControlOps();
        using var vm = Make(store, Connected(), ops);
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new(c.Profiles) {
            ["work"] = c.Profiles["work"] with { ServerUrl = "https://different.example" }
        } });
        vm.Capacity = 8;
        await vm.SaveCommand.Execute();
        await Assert.That(vm.Message!).Contains("profile changed");
        await Assert.That(ops.PutSettingsCalls).IsEqualTo(0);
        await Assert.That(ConfigMutator.LoadPure(AppConfig.GetConfigPath(Config.Root)).Profiles["work"].Daemon!.MaxAgents).IsEqualTo(5);
    });

    [Test]
    public Task Rename_requires_current_idle_evidence_or_an_unreachable_daemon() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = Connected(active: 1);
        using var vm = Make(Seed(), service);
        vm.Name = "renamed";
        await Assert.That(vm.CanRename).IsFalse();
        await Assert.That(vm.RenameHint!).Contains("1 active agents");
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        await Assert.That(vm.CanRename).IsTrue();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
        await Assert.That(vm.CanRename).IsFalse();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await Assert.That(vm.CanRename).IsTrue();
    });

    [Test]
    public Task Rename_is_unavailable_off_macOS() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = Make(Seed(), Connected(), mac: false);
        vm.Name = "renamed";
        await Assert.That(vm.CanRename).IsFalse();
        await Assert.That(vm.RenameHint!).Contains("macOS");
    });

    [Test]
    [Arguments("collision")]
    [Arguments("declined")]
    [Arguments("became-busy")]
    public Task Refused_rename_never_writes_or_enters_the_lane(string mode) => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        var service = Connected();
        var runs = 0;
        using var vm = Make(store, service, target: (_, _) => Task.FromResult(mode == "collision"),
            confirm: (_, _) => {
                if (mode == "became-busy") service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 1));
                return Task.FromResult(mode != "declined");
            }, run: (_, _) => { runs++; return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()); });
        vm.Name = "renamed";
        await vm.RenameCommand.Execute();
        await Assert.That(store.Load().Name).IsEqualTo("daemon-a");
        await Assert.That(runs).IsEqualTo(0);
    });

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public Task Rename_persists_then_replaces_the_old_id_and_relaunches_only_on_confirmed_success(bool bundled) => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        MutationRequest? received = null;
        string? persistedAtRun = null;
        var relaunches = 0;
        using var vm = Make(store, Connected(), run: (request, _) => {
            received = request;
            persistedAtRun = store.Load().Name;
            return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        }, relaunch: _ => { relaunches++; return Task.FromResult(bundled); });
        vm.Name = "renamed";
        await vm.RenameCommand.Execute();
        await Assert.That(persistedAtRun).IsEqualTo("renamed");
        await Assert.That(received!.DaemonName).IsEqualTo("renamed");
        await Assert.That(received.RetireServiceId).IsEqualTo("daemon-a");
        await Assert.That(received.Profile).IsEqualTo("work");
        await Assert.That(received.Verb).IsEqualTo(MutationVerb.Replace);
        await Assert.That(relaunches).IsEqualTo(1);
        await Assert.That(vm.Message!).Contains(bundled ? "Relaunching" : "Restart this app");
        await Assert.That(vm.CanEdit).IsFalse();
    });

    [Test]
    public Task Failed_rename_keeps_the_new_profile_name_and_does_not_relaunch() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = Seed();
        var relaunches = 0;
        using var vm = Make(store, Connected(),
            run: (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(30, "foreign_profile", RecoverySurface.Attention)),
            relaunch: _ => { relaunches++; return Task.FromResult(true); });
        vm.Name = "renamed";
        await vm.RenameCommand.Execute();
        await Assert.That(store.Load().Name).IsEqualTo("renamed");
        await Assert.That(relaunches).IsEqualTo(0);
        await Assert.That(vm.Message!).Contains("belongs to another profile");
        await Assert.That(vm.Message!).Contains("uninstall --name daemon-a");
        await Assert.That(vm.CanEdit).IsFalse();
    });
}

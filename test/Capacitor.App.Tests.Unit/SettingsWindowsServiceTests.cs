using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

/// The way back to the Windows service after its one-time offer was declined: shown only while a Scheduled
/// Task supervises a daemon this install's service could take over
[RunOn(OS.Windows)]
[NotInParallel("AvaloniaSession")]
public class SettingsWindowsServiceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly List<LifecyclePrompt> _prompts = [];
    int _moves;

    static ServiceSnapshot Snapshot(string supervisor, bool installable) =>
        new("daemon-a", true, "running", null, null, 7, 7, false, false, Supervisor: supervisor, WindowsServiceInstallable: installable);

    SettingsViewModel MakeViewModel(ServiceSnapshot? snapshot, MutationOutcome outcome, bool confirm = true, int activeAgents = 0) {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a" } }
        } });
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1", DaemonLifecycleController.ResumeCapability]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: activeAgents));
        return new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => throw new InvalidOperationException("the move runs through the lifecycle controller"),
            (prompt, _) => { _prompts.Add(prompt); return Task.FromResult(confirm); },
            _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true),
            moveToWindowsService: _ => {
                _moves++;
                return Task.FromResult<MutationOutcome?>(outcome);
            },
            serviceStatus: _ => Task.FromResult(snapshot));
    }

    static async Task SettleAsync(Func<bool> ready) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, timeout.Token);
        }
    }

    [Test]
    public Task A_task_supervised_installable_daemon_offers_the_service_and_moves_the_daemon_to_it() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = MakeViewModel(Snapshot("windows_scheduled_task", installable: true), new MutationOutcome.Succeeded());
        await SettleAsync(() => vm.WindowsServiceOffered);

        await vm.WindowsServiceCommand.Execute();

        await Assert.That(_prompts.Single().Kind).IsEqualTo(LifecyclePrompt.KindWindowsService);
        await Assert.That(_moves).IsEqualTo(1);
        await Assert.That(vm.WindowsServiceMessage).IsEqualTo("The daemon now runs under the kcap Windows service.");
        await Assert.That(vm.WindowsServiceOffered).IsFalse();
        await Assert.That(vm.WindowsServiceVisible).IsTrue();
    });

    [Test]
    public Task The_confirmation_says_running_agents_are_resumed_under_the_service() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = MakeViewModel(Snapshot("windows_scheduled_task", installable: true), new MutationOutcome.Succeeded(), activeAgents: 2);
        await SettleAsync(() => vm.WindowsServiceOffered);

        await vm.WindowsServiceCommand.Execute();

        await Assert.That(_prompts.Single().Disclosure).IsEqualTo(DaemonLifecycleController.WindowsServiceDisclosure(2, resumes: true));
        await Assert.That(_prompts.Single().Disclosure).Contains("its 2 running agents are resumed under the service");
    });

    [Test]
    public Task A_declined_administrator_prompt_changes_nothing_and_keeps_the_offer() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = MakeViewModel(Snapshot("windows_scheduled_task", installable: true),
            new MutationOutcome.Refused("elevation_declined", RecoverySurface.Attention));
        await SettleAsync(() => vm.WindowsServiceOffered);

        await vm.WindowsServiceCommand.Execute();

        await Assert.That(vm.WindowsServiceMessage).IsEqualTo("The administrator prompt was declined; nothing changed.");
        await Assert.That(vm.WindowsServiceOffered).IsTrue();
    });

    [Test]
    public Task An_unanswered_prompt_says_so_rather_than_reporting_a_failure() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = MakeViewModel(Snapshot("windows_scheduled_task", installable: true), new MutationOutcome.UnconfirmedNoAttach());
        await SettleAsync(() => vm.WindowsServiceOffered);

        await vm.WindowsServiceCommand.Execute();

        await Assert.That(vm.WindowsServiceMessage).StartsWith("The administrator prompt was not answered in time, so nothing changed.");
        await Assert.That(vm.WindowsServiceOffered).IsTrue();
    });

    [Test]
    public Task Declining_the_confirmation_runs_nothing() => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = MakeViewModel(Snapshot("windows_scheduled_task", installable: true), new MutationOutcome.Succeeded(), confirm: false);
        await SettleAsync(() => vm.WindowsServiceOffered);

        await vm.WindowsServiceCommand.Execute();

        await Assert.That(_moves).IsEqualTo(0);
    });

    [Test]
    [Arguments("windows_service", true)]
    [Arguments("windows_scheduled_task", false)]
    public Task No_offer_when_the_service_runs_the_daemon_or_cannot(string supervisor, bool installable) => AvaloniaSession.RunOnUiAsync(async () => {
        using var vm = MakeViewModel(Snapshot(supervisor, installable), new MutationOutcome.Succeeded());
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        await Assert.That(vm.WindowsServiceVisible).IsFalse();
        await Assert.That(vm.WindowsServiceOffered).IsFalse();
    });
}

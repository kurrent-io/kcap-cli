using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SettingsWindowSmokeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public Task Window_binds_edits_status_and_command_gates() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 2));
        using var vm = new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var name = window.FindControl<TextBox>("NameInput")!;
            var capacity = window.FindControl<NumericUpDown>("CapacityInput")!;
            var save = window.FindControl<Button>("SaveButton")!;
            var rename = window.FindControl<Button>("RenameButton")!;
            await Assert.That(name.Text).IsEqualTo("daemon-a");
            await Assert.That(capacity.Value).IsEqualTo(5m);
            await Assert.That(save.IsEffectivelyEnabled).IsFalse();
            await Assert.That(window.FindControl<TextBlock>("StatusText")!.Text!).Contains("2 of 5 agents");
            name.Text = "new-name";
            capacity.Value = 8;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.Name).IsEqualTo("new-name");
            await Assert.That(vm.Capacity).IsEqualTo(8m);
            await Assert.That(save.IsEffectivelyEnabled).IsTrue();
            await Assert.That(rename.IsEffectivelyEnabled).IsFalse();
            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
            Dispatcher.UIThread.RunJobs();
            await Assert.That(rename.IsEffectivelyEnabled).IsTrue();
            await Assert.That(window.Bounds.Width).IsEqualTo(540d);
            await Assert.That(capacity.Bounds.Height).IsGreaterThan(0d);
        } finally { window.Close(); }
    });
}

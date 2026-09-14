using Avalonia.Controls;
using Avalonia.Layout;
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
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true));
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var name = window.FindControl<TextBox>("NameInput")!;
            var capacity = window.FindControl<NumericUpDown>("CapacityInput")!;
            var save = window.FindControl<Button>("SaveButton")!;
            var rename = window.FindControl<Button>("RenameButton")!;
            await Assert.That(name.Text).IsEqualTo("daemon-a");
            await Assert.That(name.Classes.Contains("kcapField")).IsTrue();
            await Assert.That(capacity.Value).IsEqualTo(5m);
            await Assert.That(capacity.Classes.Contains("kcapField")).IsTrue();
            await Assert.That(capacity.ShowButtonSpinner).IsFalse();
            await Assert.That(window.FindControl<TextBlock>("DaemonTitleText")!.LetterSpacing).IsEqualTo(-0.3);
            await Assert.That(save.Classes.Contains("kcapPrimary")).IsTrue();
            await Assert.That(rename.Classes.Contains("kcapPrimary")).IsTrue();
            await Assert.That(save.IsEffectivelyEnabled).IsFalse();
            await Assert.That(window.FindControl<TextBlock>("StatusText")!.Text).IsEqualTo("2 / 5");
            await Assert.That(ToolTip.GetTip(window.FindControl<Border>("StatusChip")!)!.ToString()!).Contains("Running as daemon-a");
            await Assert.That(ToolTip.GetTip(rename)).IsEqualTo(vm.RenameHint);
            await Assert.That(ToolTip.GetShowOnDisabled(rename)).IsTrue();
            await Assert.That(rename.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Left);
            await Assert.That(save.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Left);
            await Assert.That(window.FindControl<TextBlock>("StatusCaptionText")!.IsVisible).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("StatusCaptionText")!.Text).IsEqualTo("Agents");
            var nameError = window.FindControl<TextBlock>("NameErrorText")!;
            await Assert.That(nameError.Classes.Contains("kcapHint")).IsTrue();
            await Assert.That(nameError.LetterSpacing).IsEqualTo(0.2);
            await Assert.That(window.FindControl<TextBlock>("CapacityErrorText")!.LetterSpacing).IsEqualTo(0.2);
            await Assert.That(window.FindControl<TextBlock>("MessageText")!.LetterSpacing).IsEqualTo(0.2);
            var title = window.FindControl<TextBlock>("DaemonTitleText")!;
            var chip = window.FindControl<Border>("StatusChip")!;
            await Assert.That(title.VerticalAlignment).IsEqualTo(VerticalAlignment.Center);
            await Assert.That(chip.VerticalAlignment).IsEqualTo(VerticalAlignment.Center);
            await Assert.That(Math.Abs((title.Bounds.Y + title.Bounds.Height / 2) - (chip.Bounds.Y + chip.Bounds.Height / 2)))
                .IsLessThan(1.5);
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
            await Assert.That(window.Title).IsEqualTo("Kurrent Capacitor — Settings");
            await Assert.That(window.Bounds.Width).IsEqualTo(540d);
            await Assert.That(window.Bounds.Height).IsEqualTo(580d);
            await Assert.That(capacity.Bounds.Height).IsGreaterThan(0d);
        } finally { window.Close(); }
    });
}

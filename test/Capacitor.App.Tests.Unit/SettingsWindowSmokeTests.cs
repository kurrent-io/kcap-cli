using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
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
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            var daemonTab = window.FindControl<TabItem>("DaemonTab")!;
            var name = window.FindControl<TextBox>("NameInput")!;
            var capacity = window.FindControl<NumericUpDown>("CapacityInput")!;
            var save = window.FindControl<Button>("SaveButton")!;
            var rename = window.FindControl<Button>("RenameButton")!;
            await Assert.That(name.Text).IsEqualTo("daemon-a");
            await Assert.That(tabs.SelectedIndex).IsEqualTo(0);
            Settle(window);
            var selectedPipe = daemonTab.GetVisualDescendants().OfType<Border>()
                .Single(x => x.Name == "PART_SelectedPipe");
            await Assert.That(ReferenceEquals(selectedPipe.Background, window.FindResource("KcapInfoBrush"))).IsTrue();
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
            tabs.SelectedIndex = 2; // Daemon, Profiles (hidden — no profiles VM here), Notifications
            Dispatcher.UIThread.RunJobs();
            await Assert.That(window.FindControl<ToggleSwitch>("PermissionNotificationsToggle")!.IsEffectivelyEnabled).IsFalse();
            await Assert.That(window.FindControl<ToggleSwitch>("QuestionNotificationsToggle")!.IsEffectivelyEnabled).IsFalse();
            await Assert.That(window.FindControl<ToggleSwitch>("IdleNotificationsToggle")!.IsEffectivelyEnabled).IsFalse();
            tabs.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(name.IsEffectivelyVisible).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    public Task Profiles_tab_lists_rows_with_chip_actions_and_purple_marks() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"]  = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } },
                ["other"] = new Profile { ServerUrl = "https://other.example" }
            }
        });
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        var profiles = new ProfilesSettingsViewModel(Config.Root, tokens,
            new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, TimeProvider.System), "work",
            (_, _, _) => Task.CompletedTask, (_, _) => Task.FromResult(false));
        await profiles.RefreshAsync();
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 0));
        using var vm = new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true),
            profiles: profiles);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            var tab  = window.FindControl<TabItem>("ProfilesTab")!;
            tabs.SelectedItem = tab;
            Dispatcher.UIThread.RunJobs();
            Settle(window);

            // A TabItem's own visual tree is its header only; the selected tab's content is hosted
            // by the TabControl's PART_SelectedContentHost, so the row content is found from tabs.
            var chips = tabs.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("kcapChip")).ToList();
            await Assert.That(chips.Any(b => b.Content as string == "Sign in")).IsTrue();
            await Assert.That(chips.Any(b => b.Content as string == "Remove" && b.IsVisible)).IsTrue();

            var marks = tabs.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("profileMark") && t.IsVisible).ToList();
            await Assert.That(marks.Count).IsEqualTo(2); // Active and This app, both on "work"
            foreach (var mark in marks)
                await Assert.That(ReferenceEquals(mark.Foreground, window.FindResource("KcapPurpleBrush"))).IsTrue();

            var statuses = tabs.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("profileStatus")).Select(t => t.Text).ToList();
            await Assert.That(statuses).Contains("Signed out");
        } finally {
            window.Close();
        }
    });

    [Test]
    public Task Notification_toggle_can_be_dragged_without_crashing() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        using var notifications = new NotificationSettingsService(Config.PathTo("notifications.json"));
        using var vm = MakeViewModel(notifications);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2; // Daemon, Profiles (hidden), Notifications
            Settle(window);
            var permissions = window.FindControl<ToggleSwitch>("PermissionNotificationsToggle")!;

            Drag(window, permissions, permissions.Bounds.Width - 9, -24);

            await WaitUntilAsync(() => !notifications.Current.Permissions);
            await Assert.That(permissions.IsChecked).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task Notification_tab_switches_apply_and_survive_reopening() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        var path = Config.PathTo("notifications.json");
        using var notifications = new NotificationSettingsService(path);

        using (var vm = MakeViewModel(notifications)) {
            var window = new SettingsWindow { DataContext = vm };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var tabs = window.FindControl<TabControl>("SettingsTabs")!;
                tabs.SelectedIndex = 2; // Daemon, Profiles (hidden), Notifications
                Dispatcher.UIThread.RunJobs();
                var permissions = window.FindControl<ToggleSwitch>("PermissionNotificationsToggle")!;
                var questions = window.FindControl<ToggleSwitch>("QuestionNotificationsToggle")!;
                var idle = window.FindControl<ToggleSwitch>("IdleNotificationsToggle")!;
                await Assert.That(permissions.Classes.Contains("kcapSwitch")).IsTrue();
                await Assert.That(permissions.IsEffectivelyVisible).IsTrue();
                await Assert.That(permissions.IsEffectivelyEnabled).IsTrue();
                Click(window, permissions);
                questions.Focus(NavigationMethod.Tab);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Settle(window);
                await Assert.That(questions.Classes.Contains(":focus-visible")).IsTrue();
                var focusedTrack = questions.GetVisualDescendants().OfType<Border>()
                    .Single(x => x.Name == "SwitchTrack");
                await Assert.That(focusedTrack.BorderThickness.Left).IsEqualTo(2d);
                Click(window, idle);
                await WaitUntilAsync(() => notifications.Current == new NotificationPreferences(false, false, false));
            } finally { window.Close(); }
        }

        await WaitUntilAsync(() => {
            using var saved = new NotificationSettingsService(path);
            return saved.Current == new NotificationPreferences(false, false, false);
        });

        using (var reopenedVm = MakeViewModel(notifications)) {
            var reopened = new SettingsWindow { DataContext = reopenedVm };
            try {
                reopened.Show();
                Dispatcher.UIThread.RunJobs();
                reopened.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2; // Daemon, Profiles (hidden), Notifications
                Dispatcher.UIThread.RunJobs();
                await Assert.That(reopened.FindControl<ToggleSwitch>("PermissionNotificationsToggle")!.IsChecked).IsFalse();
                await Assert.That(reopened.FindControl<ToggleSwitch>("QuestionNotificationsToggle")!.IsChecked).IsFalse();
                await Assert.That(reopened.FindControl<ToggleSwitch>("IdleNotificationsToggle")!.IsChecked).IsFalse();
                await Assert.That(reopened.Bounds.Width).IsEqualTo(540d);
                await Assert.That(reopened.Bounds.Height).IsEqualTo(580d);
            } finally { reopened.Close(); }
        }
    });

    SettingsViewModel MakeViewModel(NotificationSettingsService notifications) {
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        return new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask,
            (_, _) => Task.FromResult(true), notificationSettings: notifications);
    }

    static async Task WaitUntilAsync(Func<bool> ready) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    static void Settle(Window window) {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    static void Click(Window window, Control target) {
        Settle(window);
        var centre = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Click target is not under the window.");
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Settle(window);
    }

    static void Drag(Window window, Control target, double startX, double deltaX) {
        Settle(window);
        var start = target.TranslatePoint(new Point(startX, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Drag target is not under the window.");
        var end = new Point(start.X + deltaX, start.Y);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(end);
        window.MouseUp(end, MouseButton.Left);
        Settle(window);
    }
}

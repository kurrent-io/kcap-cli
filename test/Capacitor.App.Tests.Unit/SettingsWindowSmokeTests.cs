using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Notifications;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;
using NSubstitute;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SettingsWindowSmokeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public Task Sidebar_supports_mouse_keyboard_and_small_window_navigation() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        using var notifications = new NotificationSettingsService(Config.PathTo("notifications.json"));
        using var vm = MakeViewModel(notifications);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Settle(window);
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            var daemon = window.FindControl<TabItem>("DaemonTab")!;
            var profiles = window.FindControl<TabItem>("ProfilesTab")!;
            var alerts = window.FindControl<TabItem>("NotificationsTab")!;
            await Assert.That(window.CanResize).IsTrue();
            await Assert.That(profiles.IsEffectivelyVisible).IsFalse();
            var daemonPosition = daemon.TranslatePoint(default, window)!.Value;
            var alertsPosition = alerts.TranslatePoint(default, window)!.Value;
            await Assert.That(alertsPosition.X).IsEqualTo(daemonPosition.X);
            await Assert.That(alertsPosition.Y).IsLessThan(daemonPosition.Y);

            Click(window, alerts);
            await Assert.That(tabs.SelectedItem).IsEqualTo(alerts);
            await Assert.That(window.FindControl<ToggleSwitch>("PermissionNotificationsToggle")!.IsEffectivelyVisible).IsTrue();
            alerts.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Settle(window);
            await Assert.That(tabs.SelectedItem).IsEqualTo(window.FindControl<TabItem>("CaptureTab"));

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            Settle(window);
            Click(window, alerts);
            var toggle = window.FindControl<ToggleSwitch>("IdleNotificationsToggle")!;
            var bottom = toggle.TranslatePoint(new Point(toggle.Bounds.Width, toggle.Bounds.Height), window)!.Value;
            await Assert.That(bottom.X).IsLessThan(window.ClientSize.Width);
            await Assert.That(bottom.Y).IsLessThan(window.ClientSize.Height);
        } finally { window.Close(); }
    });

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
            var name = window.FindControl<TextBox>("NameInput")!;
            var capacity = window.FindControl<NumericUpDown>("CapacityInput")!;
            var save = window.FindControl<Button>("SaveButton")!;
            var rename = window.FindControl<Button>("RenameButton")!;
            await Assert.That(name.Text).IsEqualTo("daemon-a");
            tabs.SelectedItem = window.FindControl<TabItem>("DaemonTab");
            Settle(window);
            await Assert.That(window.FindControl<TabItem>("SearchTab")!.IsVisible).IsFalse();
            Settle(window);
            await Assert.That(capacity.Value).IsEqualTo(5m);
            await Assert.That(capacity.ShowButtonSpinner).IsFalse();
            await Assert.That(save.IsEffectivelyEnabled).IsFalse();
            await Assert.That(window.FindControl<TextBlock>("StatusText")!.Text).IsEqualTo("2 / 5");
            await Assert.That(ToolTip.GetTip(window.FindControl<Border>("StatusChip")!)!.ToString()!).Contains("Running as daemon-a");
            await Assert.That(ToolTip.GetTip(rename)).IsEqualTo(vm.RenameHint);
            await Assert.That(ToolTip.GetShowOnDisabled(rename)).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("StatusCaptionText")!.IsVisible).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("StatusCaptionText")!.Text).IsEqualTo("Agents");
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
            await Assert.That(window.Title).IsEqualTo("kcap — Settings");
            await Assert.That(window.Bounds.Width).IsEqualTo(860d);
            await Assert.That(window.Bounds.Height).IsEqualTo(640d);
            await Assert.That(capacity.Bounds.Height).IsGreaterThan(0d);
            tabs.SelectedItem = window.FindControl<TabItem>("NotificationsTab");
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
    public Task Profiles_tab_lists_rows_with_chip_actions_and_profile_marks() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with {
            ActiveProfile = "work",
            Profiles = new() {
                ["work"]  = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } },
                ["other"] = new Profile { ServerUrl = "https://other.example" }
            }
        });
        var tokens = AuthFixtures.NewTokenStore(Config.Root);
        var profiles = new ProfilesSettingsViewModel(Config.Root, tokens,
            new OnboardingGate(Config.Root, tokens, ProfileOverrides.None, NoLocalInstallation.Root, TimeProvider.System), "work",
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

            var marks = tabs.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("profileMark") && t.IsEffectivelyVisible)
                .Select(t => t.Text).ToList();
            await Assert.That(marks.Count).IsEqualTo(2);
            await Assert.That(marks).Contains("Active");
            await Assert.That(marks).Contains("This app");

            var statuses = tabs.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("profileStatus")).Select(t => t.Text).ToList();
            await Assert.That(statuses).Contains("Signed out");
        } finally {
            window.Close();
        }
    });

    [Test]
    public Task Accounts_tab_lists_rows_with_the_default_pill_state_and_menu() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1", "accounts/1"]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        var accounts = new FakeAccountsService();
        accounts.Publish(new AccountsSnapshotDto("host-1", 3, [
            new("a1", "claude", "/u/.claude", "/u/.claude", "home", "recording"),
            new("a2", "claude", "/u/.claude-work", "/u/.claude-work", "work", "recording"),
            new("a3", "codex", "/u/.codex-main", "/u/.codex-main", "main", "installed"),
        ], [new("claude", "/u/.claude", "/u/.claude"), new("codex", "/u/.codex", "/u/.codex")]));
        var requests = new List<MutationRequest>();
        var discoveries = 0;
        var accountsVm = new AccountsSettingsViewModel(accounts,
            (request, _) => { requests.Add(request); return Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()); },
            "daemon-a", "work",
            () => {
                Interlocked.Increment(ref discoveries);
                return [new AccountCandidate(HarnessId.Codex, "/u/.codex-alt", "home directory")];
            },
            _ => Task.FromResult<string?>(null), service.Status);
        using var vm = new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true)) {
            Accounts = accountsVm,
        };
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;
            var tab = window.FindControl<TabItem>("AccountsTab")!;
            await Assert.That(tab.IsVisible).IsTrue();

            tabs.SelectedItem = tab;
            Settle(window);

            var rows = tabs.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("accountRow")).ToList();
            string Label(Control row) => row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("accountLabel")).Text!;
            Border RowOf(string label) => rows.Single(r => Label(r) == label);
            bool HasPill(Control row) => row.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("accountDefault") && b.IsEffectivelyVisible);
            TextBlock State(Control row) => row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("accountState"));
            Button? Visible(Control row, string kind) =>
                row.GetVisualDescendants().OfType<Button>().SingleOrDefault(b => b.Classes.Contains(kind) && b.IsEffectivelyVisible);

            await Assert.That(string.Join(",", rows.Select(Label))).IsEqualTo("home,work,main,.codex");
            await Assert.That(HasPill(RowOf("home"))).IsTrue();
            await Assert.That(HasPill(RowOf("work"))).IsFalse();
            await Assert.That(HasPill(RowOf("main"))).IsFalse();
            await Assert.That(HasPill(RowOf(".codex"))).IsTrue();

            var trust = State(RowOf("main"));
            await Assert.That(trust.Text).IsEqualTo("Needs trust");
            await Assert.That(trust.Foreground).IsSameReferenceAs(window.FindResource("KcapWarningBrush"));
            await Assert.That(ToolTip.GetTip(trust)).IsEqualTo("Trust the kcap hooks the next time you start Codex with CODEX_HOME=/u/.codex-main");
            await Assert.That(State(RowOf("home")).Text).IsEqualTo("Recording");
            await Assert.That(State(RowOf("home")).Foreground).IsSameReferenceAs(window.FindResource("KcapMutedBrush"));
            await Assert.That(Visible(RowOf(".codex"), "accountMenu")).IsNull();
            await Assert.That(Visible(RowOf("home"), "accountAdd")).IsNull();

            var menuButton = Visible(RowOf("work"), "accountMenu")!;
            await Assert.That(AutomationProperties.GetName(menuButton)).IsEqualTo("Actions for work");
            var menu = (Flyout)menuButton.Flyout!;
            menuButton.BringIntoView();
            Click(window, menuButton);
            await Assert.That(menu.IsOpen).IsTrue();
            var items = ((Control)menu.Content!).GetVisualDescendants().OfType<Button>().ToList();
            await Assert.That(string.Join(",", items.Select(b => b.Content))).IsEqualTo("Rename,Reconnect,Remove");
            await Assert.That(items.All(b => b.Classes.Contains("kcapGhost"))).IsTrue();
            await Assert.That(items[0].FindAncestorOfType<FlyoutPresenter>()!.Classes.Contains("kcapPanel")).IsTrue();
            MainWindowSmokeTests.ClickFlyoutButton(items[0]);
            Settle(window);
            await Assert.That(menu.IsOpen).IsFalse();
            await Assert.That(accountsVm.Renaming!.Id).IsEqualTo("a2");
            var field = RowOf("work").GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("kcapField"));
            await Assert.That(field.IsEffectivelyVisible).IsTrue();
            await Assert.That(field.Text).IsEqualTo("work");

            var add = Visible(RowOf(".codex"), "accountAdd")!;
            add.BringIntoView();
            Click(window, add);
            await WaitUntilAsync(() => requests.Count == 1);
            await Assert.That(requests[0].Verb).IsEqualTo(MutationVerb.AccountAdd);
            await Assert.That(requests[0].Account).IsEqualTo(new AccountArgs("codex", "/u/.codex", null, null));

            var addAccount = tabs.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AddAccountButton");
            addAccount.BringIntoView();
            Click(window, addAccount);
            var addMenu = (Flyout)addAccount.Flyout!;
            await Assert.That(addMenu.IsOpen).IsTrue();
            await WaitUntilAsync(() => ((Control)addMenu.Content!).GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "/u/.codex-alt"));
            var texts = ((Control)addMenu.Content!).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            await Assert.That(texts).Contains("home directory");
            await Assert.That(texts).Contains("Choose a Claude directory…");
            await Assert.That(texts).Contains("Choose a Codex directory…");
            await Assert.That(discoveries).IsEqualTo(1);
            var candidate = ((Control)addMenu.Content!).GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == "/u/.codex-alt").FindAncestorOfType<Button>()!;
            MainWindowSmokeTests.ClickFlyoutButton(candidate);
            await WaitUntilAsync(() => requests.Count == 2);
            await Assert.That(addMenu.IsOpen).IsFalse();
            await Assert.That(requests[1].Account).IsEqualTo(new AccountArgs("codex", "/u/.codex-alt", null, null));
        } finally { window.Close(); }
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
            window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabItem>("NotificationsTab");
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
                tabs.SelectedItem = window.FindControl<TabItem>("NotificationsTab");
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
                reopened.FindControl<TabControl>("SettingsTabs")!.SelectedItem = reopened.FindControl<TabItem>("NotificationsTab");
                Dispatcher.UIThread.RunJobs();
                await Assert.That(reopened.FindControl<ToggleSwitch>("PermissionNotificationsToggle")!.IsChecked).IsFalse();
                await Assert.That(reopened.FindControl<ToggleSwitch>("QuestionNotificationsToggle")!.IsChecked).IsFalse();
                await Assert.That(reopened.FindControl<ToggleSwitch>("IdleNotificationsToggle")!.IsChecked).IsFalse();
            } finally { reopened.Close(); }
        }
    });

    [Test]
    public Task Blocked_notifications_show_a_notice_whose_button_opens_system_settings() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        using var notifications = new NotificationSettingsService(Config.PathTo("notifications.json"));
        var access = new FakeDesktopNotificationAccess(DesktopNotificationAccess.Denied);
        using var vm = MakeViewModel(notifications, access);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabItem>("NotificationsTab");
            Settle(window);
            var notice = window.FindControl<Border>("NotificationAccessNotice")!;
            var button = window.FindControl<Button>("NotificationAccessButton")!;
            await WaitUntilAsync(() => notice.IsEffectivelyVisible);
            await Assert.That(button.Content).IsEqualTo($"Open {SettingsViewModel.SystemSettingsName}");

            Click(window, button);

            await WaitUntilAsync(() => access.SettingsOpened == 1);
            access.Current = DesktopNotificationAccess.Allowed;
            vm.RefreshNotificationAccess();
            await WaitUntilAsync(() => !notice.IsEffectivelyVisible);
        } finally { window.Close(); }
    });

    [Test]
    public Task Capture_page_saves_edited_visibility_and_scope_through_the_view() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example" }
        } });
        using var notifications = new NotificationSettingsService(Config.PathTo("notifications.json"));
        using var vm = MakeViewModel(notifications);
        var window = new SettingsWindow { DataContext = vm, Width = 760, Height = 520 };
        try {
            window.Show();
            Click(window, window.FindControl<TabItem>("CaptureTab")!);
            var view = window.GetVisualDescendants().OfType<CaptureSettingsView>().Single();
            var visibility = view.FindControl<ListBox>("VisibilityInput")!;
            visibility.SelectedValue = "private";
            var repos = view.FindControl<TextBox>("AllowedReposInput")!;
            repos.Text = "team/project";
            Settle(window);
            await Assert.That(vm.Capture.Visibility).IsEqualTo("private");
            await Assert.That(vm.Capture.AllowedRepos).IsEqualTo("team/project");
            var save = view.FindControl<Button>("SaveCaptureButton")!;
            save.BringIntoView();
            Settle(window);
            Click(window, save);
            await WaitUntilAsync(() => !vm.Capture.Busy && vm.Capture.Message?.Contains("saved") == true);
            var saved = new SettingsProfileStore(Config.Root, "work", "https://work.example").LoadProfile();
            await Assert.That(saved.DefaultVisibility).IsEqualTo("private");
            await Assert.That(saved.AllowedRepos).IsEquivalentTo(new[] { "team/project" });
        } finally { window.Close(); }
    });

    [Test]
    public Task Harness_page_routes_connect_and_rescan_buttons_and_blocks_rename_during_setup() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example" }
        } });
        var cli = Substitute.For<IKcapCli>();
        var completion = new TaskCompletionSource<ProcessResult>();
        cli.PluginInstallAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>()).Returns(completion.Task);
        var scans = 0;
        var wired = false;
        cli.PluginRemoveAsync("--codex", Arg.Any<CancellationToken>()).Returns(_ => {
            wired = false;
            return new ProcessResult(0, "", "", false);
        });
        var harnesses = new HarnessesSettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"), cli, _ => {
            scans++;
            return Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> {
                [HarnessId.Codex] = new(true, false)
            });
        }, isWired: _ => wired);
        using var notifications = new NotificationSettingsService(Config.PathTo("notifications.json"));
        using var vm = MakeViewModel(notifications, harnesses: harnesses);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Click(window, window.FindControl<TabItem>("HarnessesTab")!);
            var view = window.GetVisualDescendants().OfType<HarnessesSettingsView>().Single();
            Click(window, view.FindControl<Button>("RescanHarnessesButton")!);
            await WaitUntilAsync(() => harnesses.Rows.Count == 1);
            Settle(window);
            var connect = view.GetVisualDescendants().OfType<CheckBox>().Single();
            Click(window, connect);
            await Assert.That(harnesses.Busy).IsTrue();
            await Assert.That(vm.CanEdit).IsFalse();
            await Assert.That(view.FindControl<Button>("RescanHarnessesButton")!.IsEffectivelyEnabled).IsFalse();
            wired = true;
            completion.SetResult(new ProcessResult(0, "", "", false));
            await WaitUntilAsync(() => !harnesses.Busy);
            await Assert.That(harnesses.Rows[0].Message!).Contains("Setup completed");
            Settle(window);
            await Assert.That(connect.IsChecked).IsTrue();
            connect.Focus(NavigationMethod.Tab);
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            await WaitUntilAsync(() => !harnesses.Busy);
            Settle(window);
            await Assert.That(connect.IsChecked).IsFalse();
            await Assert.That(harnesses.Rows[0].Message!).Contains("Disconnected");
            Click(window, view.FindControl<Button>("RescanHarnessesButton")!);
            await WaitUntilAsync(() => scans == 2);
        } finally { completion.TrySetResult(new ProcessResult(1, "", "canceled", false)); window.Close(); }
    });

    [Test]
    public Task Failed_harness_disconnect_restores_the_checked_state_for_retry() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example" }
        } });
        var cli = Substitute.For<IKcapCli>();
        cli.PluginRemoveAsync("--codex", Arg.Any<CancellationToken>()).Returns(new ProcessResult(1, "", "Could not remove hooks", false));
        var harnesses = new HarnessesSettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"), cli,
            _ => Task.FromResult<IReadOnlyDictionary<HarnessId, DetectedAgent>>(new Dictionary<HarnessId, DetectedAgent> {
                [HarnessId.Codex] = new(true, true)
            }), isWired: _ => true);
        using var notifications = new NotificationSettingsService(Config.PathTo("notifications.json"));
        using var vm = MakeViewModel(notifications, harnesses: harnesses);
        await harnesses.RescanAsync();
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Click(window, window.FindControl<TabItem>("HarnessesTab")!);
            var checkbox = window.GetVisualDescendants().OfType<CheckBox>().Single();
            Click(window, checkbox);
            await WaitUntilAsync(() => !harnesses.Busy);
            Settle(window);
            await Assert.That(checkbox.IsChecked).IsTrue();
            await Assert.That(checkbox.IsEffectivelyEnabled).IsTrue();
            await Assert.That(harnesses.Rows[0].Message).IsEqualTo("Could not remove hooks");
        } finally { window.Close(); }
    });

    SettingsViewModel MakeViewModel(NotificationSettingsService notifications, IDesktopNotificationAccess? access = null, HarnessesSettingsViewModel? harnesses = null) {
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        return new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask,
            (_, _) => Task.FromResult(true), notificationSettings: notifications, notificationAccess: access, harnesses: harnesses);
    }

    static async Task WaitUntilAsync(Func<bool> ready) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    /// <summary>With a local server, Settings carries its search choice, and opening it for search lands on that
    /// tab. Every line of the choice fits inside the tab: the longest option label wraps rather than clipping.</summary>
    [Test]
    public Task The_search_tab_holds_the_local_search_choice_within_its_width() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "http://localhost:5000", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        var cli = new FakeKcapCli();
        LocalUpgradeController? upgrade = null;
        var search = new LocalSearchController(() => cli, action => action(), held: () => upgrade?.Running == true);
        upgrade = new LocalUpgradeController(() => cli, action => action(), held: () => search.Running);
        LocalEmbeddingModelManifest offered = new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]);
        using var local = new LocalSearchViewModel(() => LocalEmbeddingSetting.Ollama("mxbai-embed-large"),
            _ => Task.FromResult<LocalEmbeddingModelManifest?>(offered),
            new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connected)), search, upgrade, () => Task.CompletedTask,
            CancellationToken.None, ImmediateScheduler.Instance);
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 0));
        using var vm = new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "http://localhost:5000"),
            service, new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true)) {
            Search = local,
        };
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            window.ShowSearch();
            Settle(window);
            var tabs = window.FindControl<TabControl>("SettingsTabs")!;

            await Assert.That(window.FindControl<TabItem>("SearchTab")!.IsVisible).IsTrue();
            await Assert.That(tabs.SelectedItem).IsSameReferenceAs(window.FindControl<TabItem>("SearchTab"));
            var choice = tabs.GetVisualDescendants().OfType<SearchChoiceView>().Single();
            await Assert.That(choice.IsEffectivelyVisible).IsTrue();
            await Assert.That(tabs.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SearchApplyButton").IsEffectivelyVisible).IsTrue();
            await Assert.That(tabs.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SearchCurrentText").Text)
                .IsEqualTo("Searches by meaning with mxbai-embed-large.");

            var clipped = choice.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
                // A line wider than its box is cut off at the box, so the text's own layout is what overflows.
                .Where(t => t.TextLayout.Width > t.Bounds.Width + 0.5
                         || t.TranslatePoint(new Point(t.Bounds.Width, 0), choice) is not { } right || right.X > choice.Bounds.Width + 0.5)
                .Select(t => t.Text).ToList();
            await Assert.That(clipped).IsEmpty();
        } finally { window.Close(); }
    });

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

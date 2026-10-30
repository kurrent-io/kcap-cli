using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using ReactiveUI.Reactive;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using Lucide.Avalonia;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Remote.Models;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the launcher pane. LauncherPaneView is a UserControl,
/// not a Window (unlike MainWindow) — each test hosts it inside a plain Window purely to give
/// headless something to Show(); session setup and control lookup otherwise copy
/// MainWindowSmokeTests exactly (see that file's own header comment).
public class LauncherPaneViewSmokeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Retrying_a_failed_upgrade_replaces_the_failure_icon_with_progress() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var cli = new FakeKcapCli {
                LocalUpgradeBehavior = UpgradeScript.Prints(2, UpgradeScript.Planned("1.2.0", "1.3.0"),
                    UpgradeScript.Switched("1.3.0"), UpgradeScript.Failed("start_failed", "did not start", true)),
            };
            var upgrade = new LocalUpgradeController(() => cli, action => action());
            using var status = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Retrying));
            var start = new TaskCompletionSource<ProcessResult>();
            using var stack = new LocalStackViewModel(() => "1.3.0", status, upgrade, _ => start.Task,
                () => Task.CompletedTask, CancellationToken.None, System.Reactive.Concurrency.ImmediateScheduler.Instance);
            var tmp = TempDir.WithPathTo("app-state.json", out var path);
            using var cleanup = tmp;
            using var home = new HomeViewModel(new FakeDaemonClientService(), new AppStateStore(path),
                new RecordingLaunchClient(), () => Task.FromResult(Array.Empty<string>()), TimeProvider.System) { LocalStack = stack };
            await upgrade.StartAsync(CancellationToken.None);
            await upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var window = new Window { Content = new LauncherPaneView { DataContext = home } };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var failed = Find<AppIcon>(window, "LocalUpgradeFailedIcon")!;
                var busy = Find<Panel>(window, "LocalUpgradeIcon")!.Children.OfType<AppIcon>()
                    .Single(icon => icon.Kind == LucideIconKind.LoaderCircle);
                await Assert.That(failed.IsEffectivelyVisible).IsTrue();
                await Assert.That(busy.IsEffectivelyVisible).IsFalse();

                var run = stack.StartCommand.Execute().ToTask();
                Dispatcher.UIThread.RunJobs();
                try {
                    await Assert.That(failed.IsEffectivelyVisible).IsFalse();
                    await Assert.That(busy.IsEffectivelyVisible).IsTrue();
                    await Assert.That(Find<TextBlock>(window, "LocalUpgradeHeadline")!.Text).IsEqualTo("Starting the local server…");
                } finally {
                    start.TrySetResult(new ProcessResult(1, "", "still unavailable", false));
                    await run;
                }
                Dispatcher.UIThread.RunJobs();
                await Assert.That(failed.IsEffectivelyVisible).IsTrue();
                await Assert.That(busy.IsEffectivelyVisible).IsFalse();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    [Test]
    public async Task Picker_search_matches_label_and_slug() {
        var m = new ModelChoice("github-copilot/claude-opus-5", "Claude Opus 5 · github-copilot");
        await Assert.That(LauncherPaneView.RowMatches(m, "opus")).IsTrue();
        await Assert.That(LauncherPaneView.RowMatches(m, "GitHub-Copilot/")).IsTrue();
        await Assert.That(LauncherPaneView.RowMatches(m, "anthropic")).IsFalse();
    }

    /// Real-shaped agent ids (Guid("N"), 32 hex digits): a Started outcome carrying anything else
    /// is HomeViewModel's "launched but unopenable" error, which would keep StartErrorText visible.
    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    const string SecondLaunchedId = "fedcba9876543210fedcba9876543210";

    sealed class RecordingLaunchClient : ILaunchClient {
        public LaunchOutcome Next = new(true, LaunchedId, null);
        public int StartCount;

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            StartCount++;
            return Task.FromResult(Next);
        }
    }

    static (HomeViewModel Vm, FakeDaemonClientService Service, RecordingLaunchClient Launch, TempDir Tmp) Build() {
        var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var service = new FakeDaemonClientService();
        // Connected steady state: StartCommand's canExecute gates on daemon + server both up.
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var launch = new RecordingLaunchClient();
        var vm = new HomeViewModel(service, new AppStateStore(path), launch, () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
        return (vm, service, launch, tmp);
    }

    static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Headline_keeps_a_fixed_question_and_a_repo_subtitle() {
        var (question, subtitleBefore, subtitleVisibleBefore, subtitleAfter, subtitleVisibleAfter) =
            await AvaloniaSession.DispatchAsync(async () => {
                var (vm, _, _, tmp) = Build();
                using var _tmp = tmp;
                var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var headline = Find<TextBlock>(window, "LauncherHeadline")!;
                var subtitle = Find<TextBlock>(window, "LauncherRepoSubtitle")!;
                var beforeText = subtitle.Text;
                var beforeVisible = subtitle.IsVisible;

                await vm.SelectRepositoryAsync("/repos/kcap-New-Machine");
                Dispatcher.UIThread.RunJobs();

                var afterText = subtitle.Text;
                var afterVisible = subtitle.IsVisible;
                var q = headline.Text;

                window.Close();
                Dispatcher.UIThread.RunJobs();
                vm.Dispose();
                return (q, beforeText, beforeVisible, afterText, afterVisible);
            });

        await Assert.That(question).IsEqualTo("What should we build?");
        await Assert.That(subtitleVisibleBefore).IsFalse();
        await Assert.That(subtitleBefore ?? "").IsEqualTo("");
        await Assert.That(subtitleVisibleAfter).IsTrue();
        await Assert.That(subtitleAfter).IsEqualTo("kcap-New-Machine");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_notice_and_sign_in_button_follow_the_server_connection() {
        var (noticeBefore, signInBefore, noticeAfter, noticeText, signInAfter, busyAfter) = await AvaloniaSession.DispatchAsync(() => {
            var (vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var notice = Find<TextBlock>(window, "BannerMessageText")!;
            var signIn = Find<Button>(window, "HomeSignInButton")!;
            var busy = Find<AppIcon>(window, "BannerBusyIcon")!;
            // The banner Border owns visibility; the text/button stay in the tree.
            var banner = notice.FindAncestorOfType<Border>()!;
            var before = (banner.IsVisible, signIn.IsVisible);

            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            Dispatcher.UIThread.RunJobs();
            var after = (banner.IsVisible, notice.Text, signIn.IsVisible, busy.IsVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before.Item1, before.Item2, after.Item1, after.Item2, after.Item3, after.Item4);
        });

        await Assert.That(noticeBefore).IsFalse();
        await Assert.That(signInBefore).IsFalse();
        await Assert.That(noticeAfter).IsTrue();
        await Assert.That(noticeText).IsEqualTo(NoticeText.Headline(HomeViewModel.ServerLostNotice).Title);
        await Assert.That(signInAfter).IsTrue();
        await Assert.That(busyAfter).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task After_sign_in_the_banner_shows_a_loader_until_the_daemon_reconnects() {
        var (text, signIn, busy) = await AvaloniaSession.DispatchAsync(() => {
            var path = Tmp.PathTo("app-state.json");
            var daemon = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            using var vm = new HomeViewModel(
                daemon, new AppStateStore(path), new RecordingLaunchClient(),
                () => Task.FromResult(Array.Empty<string>()), TimeProvider.System, laneStatus: lane.Status,
                appServerUrl: "http://localhost:9999");
            daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm.NotifySignInCompleted();
                lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
                Dispatcher.UIThread.RunJobs();

                var notice = Find<TextBlock>(window, "BannerMessageText")!;
                var signInBtn = Find<Button>(window, "HomeSignInButton")!;
                var busyIcon = Find<AppIcon>(window, "BannerBusyIcon")!;
                return (notice.Text, signInBtn.IsVisible, busyIcon.IsVisible);
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

        await Assert.That(text).IsEqualTo("Finishing sign-in…");
        await Assert.That(signIn).IsFalse();
        await Assert.That(busy).IsTrue();
    }

    /// The action row stays out of the layout until it has something to show, and a busy banner trades
    /// its warning glyph for a spinner. Actions sit below the message with a clear gap, even in a wide card.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Connection_banner_stacks_primary_actions_and_drops_idle_chrome() {
        var (idle, busy) = await AvaloniaSession.DispatchAsync(() => {
            var (vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            var start = new BehaviorSubject<bool>(true);
            var retry = new BehaviorSubject<bool>(false);
            vm.AttachDaemonRecovery(
                ReactiveCommand.Create(() => { }),
                ReactiveCommand.Create(() => { }),
                start, retry, Observable.Return<string?>(null));

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var banner = Find<Border>(window, "ConnectionBanner")!;
            var text = Find<TextBlock>(window, "BannerMessageText")!;
            var button = Find<Button>(window, "StartDaemonButton")!;
            var spinner = Find<AppIcon>(window, "BannerBusyIcon")!;
            var warning = Find<AppIcon>(window, "BannerIcon")!;
            var actions = Find<StackPanel>(window, "BannerActions")!;

            static double Top(Control c, Visual rel) => c.TranslatePoint(new Point(0, 0), rel)!.Value.Y;
            static double Left(Control c, Visual rel) => c.TranslatePoint(new Point(0, 0), rel)!.Value.X;
            static double Bottom(Control c, Visual rel) => Top(c, rel) + c.Bounds.Height;

            var gap = Top(button, banner) - Bottom(Find<TextBlock>(window, "BannerBodyText")!, banner);
            var idleShot = (
                BarVisible: spinner.IsVisible,
                WarningVisible: warning.IsEffectivelyVisible,
                TextTop: Top(text, banner),
                AlignedLeft: Math.Abs(Left(button, banner) - Left(text, banner)) < 1,
                Primary: button.Classes.Contains("kcapPrimary"),
                Gap: gap);

            start.OnNext(false);
            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "connecting"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var busyShot = (
                BarVisible: spinner.IsVisible,
                WarningVisible: warning.IsEffectivelyVisible,
                ActionsVisible: actions.IsEffectivelyVisible,
                Compact: banner.Bounds.Height <= text.Bounds.Height + Find<TextBlock>(window, "BannerBodyText")!.Bounds.Height + 32);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (idleShot, busyShot);
        });

        await Assert.That(idle.BarVisible).IsFalse();
        await Assert.That(idle.WarningVisible).IsTrue();
        await Assert.That(idle.TextTop).IsLessThan(22);
        await Assert.That(idle.AlignedLeft).IsTrue();
        await Assert.That(idle.Primary).IsTrue();
        await Assert.That(idle.Gap).IsGreaterThanOrEqualTo(13);
        await Assert.That(idle.Gap).IsLessThanOrEqualTo(15);
        await Assert.That(busy.BarVisible).IsTrue();
        await Assert.That(busy.WarningVisible).IsFalse();
        await Assert.That(busy.ActionsVisible).IsFalse();
        await Assert.That(busy.Compact).IsTrue();
    }

    [Test]
    [Arguments("launchctl bootstrap failed (exit code 5).")]
    [Arguments("launchctl bootstrap failed (exit code 5).\nInput/output error")]
    [NotInParallel("AvaloniaSession")]
    public async Task Connection_banner_keeps_system_details_below_its_copy(string error) {
        var (headline, detailText, belowCopy, aboveActions, cleared) = await AvaloniaSession.DispatchAsync(() => {
            var (vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            using var message = new BehaviorSubject<string?>(error);
            vm.AttachDaemonRecovery(
                ReactiveCommand.Create(() => { }), ReactiveCommand.Create(() => { }),
                Observable.Return(true), Observable.Return(false), message);
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 480, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var copy = Find<TextBlock>(window, "BannerMessageText")!;
            var detail = Find<SelectableTextBlock>(window, "BannerDetailText");
            var button = Find<Button>(window, "StartDaemonButton")!;
            static double Top(Control c, Visual rel) => c.TranslatePoint(new Point(0, 0), rel)!.Value.Y;
            var headline = copy.Text;
            var detailText = detail?.Text;
            var below = detail is not null && Top(detail, window) >= Top(copy, window) + copy.Bounds.Height + 3;
            var above = detail is not null && Top(button, window) >= Top(detail, window) + detail.Bounds.Height + 13;

            message.OnNext(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var body = Find<TextBlock>(window, "BannerBodyText")!;
            var cleared = detail is not null && !detail.IsVisible
                && Math.Abs(Top(button, window) - Top(body, window) - body.Bounds.Height - 14) < 1;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (headline, detailText, below, above, cleared);
        });
        await Assert.That(headline).IsEqualTo(NoticeText.Headline(HomeViewModel.DaemonDownNotice).Title);
        await Assert.That(detailText).IsEqualTo(error);
        await Assert.That(belowCopy).IsTrue();
        await Assert.That(aboveActions).IsTrue();
        await Assert.That(cleared).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task PermissionChip_shows_for_claude_and_hides_for_other_vendors() {
        var (forClaude, forCodex) = await AvaloniaSession.DispatchAsync(async () => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var chip = Find<Button>(window, "PermissionChip");
            var claude = chip?.IsVisible ?? false;

            await vm.ChooseHarnessAsync("codex");
            Dispatcher.UIThread.RunJobs();
            var codex = chip?.IsVisible ?? true;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (claude, codex);
        });

        await Assert.That(forClaude).IsTrue();
        await Assert.That(forCodex).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Codex_posture_chips_show_for_codex_and_hide_for_other_vendors() {
        var (forClaude, forCodex, labels) = await AvaloniaSession.DispatchAsync(async () => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var sandbox = Find<Button>(window, "CodexSandboxChip");
            var approval = Find<Button>(window, "CodexApprovalChip");
            var claude = (sandbox?.IsVisible ?? true) || (approval?.IsVisible ?? true);

            await vm.ChooseHarnessAsync("codex");
            Dispatcher.UIThread.RunJobs();
            var codex = (sandbox?.IsVisible ?? false) && (approval?.IsVisible ?? false);
            var text = (sandbox?.Content as string, approval?.Content as string);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (claude, codex, text);
        });

        await Assert.That(forClaude).IsFalse();
        await Assert.That(forCodex).IsTrue();
        await Assert.That(labels.Item1).IsEqualTo("Sandbox · Workspace write");
        await Assert.That(labels.Item2).IsEqualTo("Approvals · On request");
    }

    [Test]
    public async Task A_picker_row_names_its_selection() {
        await Assert.That(LauncherPaneView.ChoiceName("High", selected: true)).IsEqualTo("High, selected");
        await Assert.That(LauncherPaneView.ChoiceName("High", selected: false)).IsEqualTo("High");
    }

    /// The button carries a glyph, not text, so assistive technology reads the automation name.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_carries_an_accessible_name() {
        var name = await AvaloniaSession.DispatchAsync(() => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var startButton = Find<Button>(window, "StartButton")!;
            var automationName = AutomationProperties.GetName(startButton);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return automationName;
        });

        await Assert.That(name).IsEqualTo("Start");
    }

    /// Disabled Start still exposes why — hover tips are suppressed on disabled controls unless
    /// ShowOnDisabled is set, and the tip text must name the missing repository gate.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_tooltip_explains_disabled_without_repository() {
        var (tipBefore, tipAfter, showOnDisabled) = await AvaloniaSession.DispatchAsync(async () => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var startButton = Find<Button>(window, "StartButton")!;
            var before = ToolTip.GetTip(startButton) as string;
            var show = ToolTip.GetShowOnDisabled(startButton);

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();
            var after = ToolTip.GetTip(startButton) as string;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, after, show);
        });

        await Assert.That(showOnDisabled).IsTrue();
        await Assert.That(tipBefore).IsEqualTo("Select a repository to start");
        await Assert.That(tipAfter).IsEqualTo("Start");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_is_enabled_only_once_a_repository_is_selected() {
        var (enabledBefore, enabledAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var startButton = Find<Button>(window, "StartButton")!;
            var before = startButton.IsEnabled;

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();
            var after = startButton.IsEnabled;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, after);
        });

        await Assert.That(enabledBefore).IsFalse();
        await Assert.That(enabledAfter).IsTrue();
    }

    /// Enter in the goal box starts the same way as the Start button — tunnel KeyDown, so the
    /// TextBox cannot swallow the key before Start sees it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_in_the_goal_box_starts_when_Start_can_run() {
        var (goalAfter, startCount) = await AvaloniaSession.DispatchAsync(async () => {
            var (vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();

            var goal = Find<TextBox>(window, "GoalInput")!;
            goal.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("ship it");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(goal.Text).IsEqualTo("ship it");

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 50 && launch.StartCount == 0; i++) {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }

            var after = vm.Goal;
            var count = launch.StartCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (after, count);
        });

        await Assert.That(startCount).IsEqualTo(1);
        await Assert.That(goalAfter).IsEqualTo("");
    }

    /// Without a repository, Enter is consumed and starts nothing — same gate as the Start button.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_without_a_repository_does_not_start() {
        var (goalAfter, startCount) = await AvaloniaSession.DispatchAsync(() => {
            var (vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var goal = Find<TextBox>(window, "GoalInput")!;
            goal.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("ship it");
            Dispatcher.UIThread.RunJobs();

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var after = goal.Text;
            var count = launch.StartCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (after, count);
        });

        await Assert.That(startCount).IsEqualTo(0);
        await Assert.That(goalAfter).IsEqualTo("ship it");
    }

    /// Shift+Enter inserts a newline instead of launching — even when Start could run — matching
    /// the chat composer. Bare Enter still launches (covered above).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Shift_Enter_in_the_goal_box_inserts_a_newline_and_does_not_start() {
        var (goalText, acceptsReturn, startCount) = await AvaloniaSession.DispatchAsync(async () => {
            var (vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            Dispatcher.UIThread.RunJobs();

            var goal = Find<TextBox>(window, "GoalInput")!;
            var accepts = goal.AcceptsReturn;
            goal.Focus();
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("line one");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
            window.KeyTextInput("line two");
            Dispatcher.UIThread.RunJobs();

            var text = goal.Text;
            var count = launch.StartCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (text, accepts, count);
        });

        await Assert.That(acceptsReturn).IsTrue();
        await Assert.That(startCount).IsEqualTo(0);
        await Assert.That(goalText).Contains("line one");
        await Assert.That(goalText).Contains("line two");
        await Assert.That(goalText!.Contains('\n')).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartErrorText_visibility_follows_StartError() {
        var (visibleBefore, visibleAfterFailure, errorMessage, visibleAfterSuccess) = await AvaloniaSession.DispatchAsync(async () => {
            var (vm, _, launch, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var errorText = Find<TextBlock>(window, "StartErrorText")!;
            var before = errorText.IsVisible;

            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");
            await vm.StartCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var afterFailure = errorText.IsVisible;
            var message = errorText.Text;

            launch.Next = new LaunchOutcome(true, SecondLaunchedId, null);
            await vm.StartCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var afterSuccess = errorText.IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, afterFailure, message, afterSuccess);
        });

        await Assert.That(visibleBefore).IsFalse();
        await Assert.That(visibleAfterFailure).IsTrue();
        await Assert.That(errorMessage).IsEqualTo("Daemon 'kcap-dev' is at capacity.");
        await Assert.That(visibleAfterSuccess).IsFalse();
    }

    /// Pins that the goal box draws no ring of its own on focus: the card is its boundary, so
    /// the theme's focused border and fill stay off.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_focused_goal_box_draws_no_ring_inside_its_card() {
        var (thickness, transparent) = await AvaloniaSession.DispatchAsync(() => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 800, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var goal = Find<TextBox>(window, "GoalInput")!;
            goal.Focus();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var ring = goal.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_BorderElement");
            var result = (ring.BorderThickness, ring.Background is null || ring.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 });
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return result;
        });

        await Assert.That(thickness).IsEqualTo(new Avalonia.Thickness(0));
        await Assert.That(transparent).IsTrue();
    }

    /// A launcher whose attachment gate is open: a connected lane for the credential the upload
    /// needs, and a local daemon advertising the capability the files ride on.
    static (HomeViewModel Vm, TempDir Tmp) BuildAttachable(IAttachmentUploader? uploader = null) {
        var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", "input/2"]));
        var vm = new HomeViewModel(
            service, new AppStateStore(path), new RecordingLaunchClient(),
            () => Task.FromResult(Array.Empty<string>()), TimeProvider.System,
            laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)),
            uploader: uploader);
        return (vm, tmp);
    }

    static List<StagedAttachmentViewModel> Chips(Window window) => window.GetVisualDescendants()
        .OfType<TextBlock>().Where(t => t.Name == "ChipName").Select(t => (StagedAttachmentViewModel)t.DataContext!).ToList();

    static void Settle(Window window) {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// Real input, not a raised ClickEvent: a Button's Command runs off its own click handling,
    /// which a synthesized routed event never reaches. Aim at the center — a corner miss is easy.
    static void Click(Window window, Control target) {
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var origin = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Click target is not under the window.");
        window.MouseDown(origin, MouseButton.Left);
        window.MouseUp(origin, MouseButton.Left);
        Settle(window);
    }

    /// The chip strip's whole wiring on the goal card: collapsed with an empty tray, one chip per
    /// staged file carrying its name and size, and the chip's own button takes that file back out.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Staged_chips_render_on_the_goal_card_and_a_chip_removes_its_own_file() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var (vm, tmp) = BuildAttachable();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Settle(window);

            var strip = Find<AttachmentChipStrip>(window, "ChipStrip")!;
            await Assert.That(strip.IsVisible).IsFalse();

            vm.Tray.AddAll([
                new StagedAttachment("a.png", "image/png", new byte[] { 1, 2, 3 }),
                new StagedAttachment("notes.txt", "text/plain", new byte[2048])]);
            Settle(window);

            await Assert.That(strip.IsVisible).IsTrue();
            await Assert.That(Chips(window).Select(c => c.FileName)).IsEquivalentTo(["a.png", "notes.txt"], CollectionOrdering.Matching);
            await Assert.That(Chips(window).Select(c => c.SizeLabel)).IsEquivalentTo(["3 B", "2 KB"], CollectionOrdering.Matching);

            var remove = strip.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ChipRemove");
            Click(window, remove);

            await Assert.That(vm.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(["notes.txt"]);
            await Assert.That(Chips(window).Select(c => c.FileName)).IsEquivalentTo(["notes.txt"]);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        });
    }

    /// The view's half of the intake: dragging over the goal card rings it, and dropping there
    /// reaches the launcher's tray and puts the ring back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_drop_on_the_goal_card_stages_the_file() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var (vm, tmp) = BuildAttachable();
            using var _tmp = tmp;
            var view = new LauncherPaneView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            Settle(window);
            await Assert.That(vm.CanAttach).IsTrue();

            var card = Find<Border>(window, "GoalCard")!;
            var resting = card.BorderBrush;
            await Assert.That(resting).IsNotNull();

            var transfer = new DataTransfer();
            var item = new DataTransferItem();
            item.SetFile(FakeStorageFile.Of("dropped.png", new byte[] { 1, 2, 3, 4 }));
            transfer.Add(item);

            card.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer, card, new Point(6, 6), KeyModifiers.None));
            Settle(window);
            // The highlight is a class the card's own style answers; a local brush on the card
            // would outrank it and the drag would look the same as no drag.
            await Assert.That(card.Classes.Contains("dragOver")).IsTrue();
            await Assert.That(card.BorderBrush).IsNotSameReferenceAs(resting);

            card.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, card, new Point(6, 6), KeyModifiers.None));
            await (view.PendingIntakeForTesting ?? Task.CompletedTask);
            Settle(window);

            await Assert.That(vm.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(["dropped.png"]);
            await Assert.That(card.Classes.Contains("dragOver")).IsFalse();
            await Assert.That(card.BorderBrush).IsSameReferenceAs(resting);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        });
    }

    /// A shut gate leaves the "+" reachable but disabled, and the tooltip that says why has to
    /// survive the disabled state — Avalonia suppresses tips on disabled controls otherwise.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task AttachButton_is_disabled_with_the_hint_when_attaching_is_not_available() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Settle(window);

            var attach = Find<Button>(window, "AttachButton")!;
            await Assert.That(attach.IsEnabled).IsFalse();
            await Assert.That(ToolTip.GetTip(attach) as string).IsEqualTo(HomeViewModel.SignInToAttach);
            await Assert.That(ToolTip.GetShowOnDisabled(attach)).IsTrue();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        });
    }

    /// Start is unusable while the files it would carry are still going up, so its tip says so
    /// rather than leaving the repository-gate wording standing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartButton_tooltip_reports_an_upload_in_flight() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var held = new TaskCompletionSource<UploadOutcome>();
            var (vm, tmp) = BuildAttachable(new ScriptedUploader { Pending = held });
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Settle(window);

            var startButton = Find<Button>(window, "StartButton")!;
            await vm.SelectRepositoryAsync("/repos/kcap-cli");
            vm.Tray.AddAll([new StagedAttachment("a.png", "image/png", new byte[] { 1, 2, 3 })]);
            Settle(window);
            await Assert.That(ToolTip.GetTip(startButton) as string).IsEqualTo("Start");

            var launching = vm.StartCommand.Execute().ToTask();
            Settle(window);
            await Assert.That(ToolTip.GetTip(startButton) as string).IsEqualTo("Uploading…");

            held.SetResult(new UploadOutcome(UploadKind.Uploaded, ["u1"], null));
            await launching;
            Settle(window);
            await Assert.That(ToolTip.GetTip(startButton) as string).IsEqualTo("Start");

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        });
    }

    static (Flyout Menu, List<AppIcon> Checks) OpenMenu(Window window, LauncherPaneView view, string chipName) {
        Click(window, Find<Button>(window, chipName)!);
        var menu = view.OpenMenuForTesting!;
        var checks = ((Control)menu.Content!).GetVisualDescendants().OfType<AppIcon>()
            .Where(i => i.Kind == Lucide.Avalonia.LucideIconKind.Check).ToList();
        return (menu, checks);
    }

    /// Pins one selection marker across the chip menus: a single visible check on the current
    /// choice, and the menu aligned to its chip's left edge rather than centred past the composer.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chip_menus_mark_the_choice_with_one_check_and_open_from_the_chip_left_edge() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var (vm, _, _, tmp) = Build();
            using var _tmp = tmp;
            var view = new LauncherPaneView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            vm.SelectedEffort = "high";

            var (effort, effortChecks) = OpenMenu(window, view, "EffortChip");
            await Assert.That(effort.Placement).IsEqualTo(PlacementMode.BottomEdgeAlignedLeft);
            await Assert.That(effortChecks.Count(c => c.IsVisible)).IsEqualTo(1);
            var checkedRow = effortChecks.Single(c => c.IsVisible).FindAncestorOfType<Button>()!;
            await Assert.That(checkedRow.GetVisualDescendants().OfType<TextBlock>().First().Text)
                .IsEqualTo(HostedHarnessCatalog.EffortLabelFor("high"));
            effort.Hide();
            Settle(window);

            var (agent, agentChecks) = OpenMenu(window, view, "AgentChip");
            await Assert.That(agent.Placement).IsEqualTo(PlacementMode.BottomEdgeAlignedLeft);
            await Assert.That(agentChecks.Count(c => c.IsVisible)).IsEqualTo(1);
            agent.Hide();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        });
    }

    /// Pins that a remote machine's repository menu never opens this machine's folder picker: its
    /// add row opens a focused path field, a relative path is refused in place, and Enter on a full
    /// path selects it and closes the field.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_machine_takes_a_typed_repository_path() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var path = Tmp.PathTo("app-state.json");
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var remote = new FakeRemoteAgents();
            remote.DaemonsSubject.OnNext([
                new Capacitor.Remote.Models.DaemonInfo { Name = "home-pc", OwnerUserId = "u1", Connected = true, RepoPaths = ["/w/repo"] },
            ]);
            var vm = new HomeViewModel(
                service, new AppStateStore(path), new RecordingLaunchClient(), () => Task.FromResult(Array.Empty<string>()),
                TimeProvider.System, daemons: remote.Daemons, viewerId: _ => Task.FromResult<string?>("u1"));
            await vm.SelectMachineAsync("home-pc", isLocal: false);
            var view = new LauncherPaneView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();

            var (menu, _) = OpenMenu(window, view, "RepositoryChip");
            var rows = ((Control)menu.Content!).GetVisualDescendants().OfType<Button>().ToList();
            await Assert.That(rows.Any(r => AutomationProperties.GetName(r) == "Add repository…")).IsFalse();
            Click(window, rows.Single(r => AutomationProperties.GetName(r) == "Enter a path on home-pc…"));

            var entry = view.OpenMenuForTesting!;
            await Assert.That(entry).IsNotSameReferenceAs(menu);
            var field = ((Control)entry.Content!).GetVisualDescendants().OfType<TextBox>().Single();
            await Assert.That(field.IsFocused).IsTrue();

            window.KeyTextInput("git/other");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle(window);
            await Assert.That(entry.IsOpen).IsTrue();
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/w/repo");

            field.Text = "/srv/other";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Settle(window);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo("/srv/other");
            await Assert.That(entry.IsOpen).IsFalse();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        });
    }

    /// Pins that a harness the daemon does not advertise says why on hover; a disabled control
    /// shows no tooltip unless it opts in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unavailable_harness_explains_itself_on_hover() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var (vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude"]));
            var view = new LauncherPaneView { DataContext = vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            Settle(window);

            var (agent, _) = OpenMenu(window, view, "AgentChip");
            var tiles = ((Control)agent.Content!).GetVisualDescendants().OfType<Button>()
                .Where(b => b.Width == 34).ToList();
            var disabled = tiles.Where(t => !t.IsEnabled).ToList();

            await Assert.That(disabled).IsNotEmpty();
            foreach (var tile in disabled) {
                await Assert.That(ToolTip.GetShowOnDisabled(tile)).IsTrue();
                await Assert.That(ToolTip.GetTip(tile) as string).Contains("not installed or not supported");
            }
            var enabled = tiles.Single(t => t.IsEnabled);
            await Assert.That(ToolTip.GetTip(enabled) as string).DoesNotContain("not installed");
            agent.Hide();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
        });
    }

    static readonly AccountViewDto HomeAccount = new("a1", "claude", "/u/.claude", "/u/.claude", "home", "recording");
    static readonly AccountViewDto WorkAccount = new("a2", "claude", "/u/.claude-work", "/u/.claude-work", "work", "broken");

    static readonly DateTimeOffset UsageNow = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    static AccountsSnapshotDto AccountsSnapshot(long revision, params AccountViewDto[] accounts) =>
        new("h1", revision, accounts, [new("claude", "/u/.claude", "/u/.claude")]);

    /// A connected local daemon the server lists under accounts/1, a repository chosen, and a
    /// snapshot of two Claude accounts: home in the default directory, work in its own.
    sealed class AccountRig : IDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeRemoteAgents Remote { get; } = new();
        public BehaviorSubject<AccountsSnapshotDto?> Snapshots { get; } = new(null);
        public RecordingLaunchClient Launch { get; } = new();
        public HomeViewModel Vm { get; }

        public AccountRig(string statePath, bool readings = false) {
            Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            Vm = new HomeViewModel(
                Daemon, new AppStateStore(statePath), Launch, () => Task.FromResult(Array.Empty<string>()), TimeProvider.System,
                viewerId: _ => Task.FromResult<string?>("u1"),
                laneStatus: Observable.Return(new ServerLaneStatus(ServerLaneState.Connected)),
                daemons: Remote.Daemons, listing: Remote.Listing, daemonAccounts: Remote.DaemonAccounts, localAccounts: Snapshots,
                readings: readings ? new AccountReadings(Snapshots, Remote.DrawableDaemonAccounts, Observable.Return(UsageNow)) : null);
        }

        public void AnswerListing() =>
            Remote.ListingSubject.OnNext(new DaemonListing([new DaemonInfo {
                Name = "daemon-a", OwnerUserId = "u1", Connected = true, SupportsAccountLaunches = true,
                SupportedVendors = ["claude", "codex"],
            }], 1, false));

        public async Task ResolveAsync() {
            await Vm.AccountMemoryLoadedForTesting;
            AnswerListing();
            await Vm.SelectRepositoryAsync("/repos/kcap-cli");
            Snapshots.OnNext(AccountsSnapshot(1, HomeAccount, WorkAccount));
            await UntilAsync(() => Vm.AccountPendingText is null && Vm.AccountSection, "the account choice resolving");
        }

        public void Dispose() {
            Vm.Dispose();
            Snapshots.Dispose();
            Remote.Dispose();
        }
    }

    static async Task UntilAsync(Func<bool> condition, string what, Window? window = null) {
        for (var i = 0; i < 200 && !condition(); i++) {
            await Task.Delay(10);
            if (window is null) Dispatcher.UIThread.RunJobs();
            else Settle(window);
        }
        if (!condition()) throw new TimeoutException($"timed out waiting for {what}");
    }

    static List<Button> PanelButtons(Flyout menu) =>
        ((Control)menu.Content!).GetVisualDescendants().OfType<Button>().ToList();

    static Button? NamedRow(Flyout menu, string name) =>
        PanelButtons(menu).SingleOrDefault(b => AutomationProperties.GetName(b) == name);

    static TextBlock? AccountHeader(Flyout menu) =>
        ((Control)menu.Content!).GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(t => t.Text == "Account");

    static List<TextBlock> Texts(Control row) => row.GetVisualDescendants().OfType<TextBlock>().ToList();

    /// Pins the Account section above the model rows: a row per account, the Default pill in neutral
    /// brushes on the daemon's default only, the directory, a state other than Recording in its
    /// attention colour, the check on the selection, and Tab from the search box reaching the first
    /// row. Picking a row from the keyboard keeps the panel open and lists it again with the check moved.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_agent_panel_lists_the_account_rows_with_the_default_pill_and_focus_reaches_them() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new AccountRig(Tmp.PathTo("app-state.json"));
            await rig.ResolveAsync();
            var view = new LauncherPaneView { DataContext = rig.Vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            Settle(window);

            var (agent, _) = OpenMenu(window, view, "AgentChip");
            Settle(window);

            await Assert.That(AccountHeader(agent)!.Classes.Contains("sectionMeta")).IsTrue();
            var home = NamedRow(agent, "home, selected")!;
            var work = NamedRow(agent, "work")!;
            var modelDefault = NamedRow(agent, "Default — Claude Code chooses, Claude Code, selected")!;
            var order = ((Panel)home.Parent!).Children;
            await Assert.That(order.IndexOf(home)).IsLessThan(order.IndexOf(work));
            await Assert.That(order.IndexOf(work)).IsLessThan(order.IndexOf(modelDefault));

            await Assert.That(Texts(home).Select(t => t.Text ?? "")).IsEquivalentTo(["home", "Default", "/u/.claude"], CollectionOrdering.Matching);
            await Assert.That(Texts(work).Select(t => t.Text ?? "")).IsEquivalentTo(["work", "/u/.claude-work", "Needs repair"], CollectionOrdering.Matching);

            var pill = (Border)Texts(home).Single(t => t.Text == "Default").Parent!;
            await Assert.That(pill.Background).IsSameReferenceAs(view.FindResource("KcapSurfaceRaisedBrush"));
            await Assert.That(pill.BorderBrush).IsSameReferenceAs(view.FindResource("KcapBorderBrush"));
            await Assert.That(((TextBlock)pill.Child!).Foreground).IsSameReferenceAs(view.FindResource("KcapMutedBrush"));
            await Assert.That(Texts(work).Single(t => t.Text == "Needs repair").Foreground).IsSameReferenceAs(view.FindResource("KcapWarningBrush"));

            static bool Checked(Button row) => row.GetVisualDescendants().OfType<AppIcon>()
                .Single(i => i.Kind == Lucide.Avalonia.LucideIconKind.Check).IsVisible;
            await Assert.That(Checked(home)).IsTrue();
            await Assert.That(Checked(work)).IsFalse();

            var search = ((Control)agent.Content!).GetVisualDescendants().OfType<TextBox>().Single();
            await Assert.That(search.IsFocused).IsTrue();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Settle(window);
            await Assert.That(home.IsFocused).IsTrue();

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Settle(window);
            await Assert.That(work.IsFocused).IsTrue();
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await UntilAsync(() => NamedRow(agent, "work, selected") is not null, "the picked row", window);
            await Assert.That(rig.Vm.SelectedAccountLabel).IsEqualTo("work");
            await Assert.That(agent.IsOpen).IsTrue();
            await Assert.That(NamedRow(agent, "home")).IsNotNull();
            await Assert.That(NamedRow(agent, "Default — Claude Code chooses, Claude Code, selected")).IsNotNull();

            agent.Hide();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// The field matches models and vendors, never accounts: typing an account's own label hides the
    /// section rather than finding the row, and clearing the term brings it back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_search_term_hides_the_account_section() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new AccountRig(Tmp.PathTo("app-state.json"));
            await rig.ResolveAsync();
            var view = new LauncherPaneView { DataContext = rig.Vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            Settle(window);

            var (agent, _) = OpenMenu(window, view, "AgentChip");
            Settle(window);
            await Assert.That(AccountHeader(agent)).IsNotNull();

            var search = ((Control)agent.Content!).GetVisualDescendants().OfType<TextBox>().Single();
            search.Text = "work";
            Settle(window);
            await Assert.That(AccountHeader(agent)).IsNull();
            await Assert.That(NamedRow(agent, "work")).IsNull();
            await Assert.That(NamedRow(agent, "Use “work”, Claude Code")).IsNotNull();

            search.Text = "";
            Settle(window);
            await Assert.That(AccountHeader(agent)).IsNotNull();
            await Assert.That(NamedRow(agent, "work")).IsNotNull();

            agent.Hide();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_panel_draws_bars_and_drops_them_when_the_host_goes_away() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new AccountRig(Tmp.PathTo("app-state.json"), readings: true);
            var box = new DaemonInfo {
                Name = "box", OwnerUserId = "u1", Connected = true, SupportsAccountLaunches = true,
                SupportedVendors = ["claude", "codex"], RepoPaths = ["/repos/kcap-cli"],
            };
            rig.Remote.DaemonsSubject.OnNext([box]);
            rig.AnswerListing();
            rig.Remote.ListingSubject.OnNext(new DaemonListing([rig.Remote.ListingSubject.Value.Daemons![0], box], 1, false));
            await rig.Vm.SelectRepositoryAsync("/repos/kcap-cli");
            await rig.Vm.SelectMachineAsync("box", isLocal: false);

            static ListedAccountDto Listed(string id, ListedUsageWindowDto? window) => new() {
                Id = id, Vendor = "claude", Directory = "/u/." + id, DirectoryKey = "/u/." + id, Label = id, RecordingState = "recording",
                Reading = window is null ? null : new ListedReadingDto { Windows = [window] },
            };
            var live = new ListedUsageWindowDto {
                LimitId = "claude", WindowMinutes = 300, UsedPercent = 42, ResetsAt = UsageNow.AddHours(2), ConfirmedAt = UsageNow,
            };
            var expired = new ListedUsageWindowDto {
                LimitId = "claude", WindowMinutes = 10080, UsedPercent = 30, ResetsAt = UsageNow.AddHours(-1), ConfirmedAt = UsageNow.AddHours(-3),
            };
            var row = new DaemonAccountsDto {
                DaemonName = "box", HostId = "h2", Revision = 1,
                Accounts = [Listed("d1", live), Listed("d2", expired)],
                Defaults = [new() { Vendor = "claude", Directory = "/u/.claude", DirectoryKey = "/u/.claude" }],
            };
            rig.Remote.DaemonAccountsSubject.OnNext([row]);
            rig.Remote.DrawableDaemonAccountsSubject.OnNext([row]);
            await UntilAsync(() => rig.Vm.AccountPendingText is null && rig.Vm.AccountSection, "the remote choice resolving");

            var view = new LauncherPaneView { DataContext = rig.Vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            Settle(window);
            var (agent, _) = OpenMenu(window, view, "AgentChip");
            Settle(window);
            static bool Shows(Flyout menu, string text) =>
                ((Control)menu.Content!).GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text);

            await Assert.That(Shows(agent, "42%")).IsTrue();
            await Assert.That(Shows(agent, "5h")).IsTrue();

            var expiredFill = NamedRow(agent, "d2")!.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Width == 46 && b.Child is Border).Child as Border;
            await Assert.That(expiredFill!.Width).IsEqualTo(46);
            await Assert.That(expiredFill.Background).IsSameReferenceAs(view.FindResource("KcapBorderStrongBrush"));

            rig.Remote.DrawableDaemonAccountsSubject.OnNext([]);
            await UntilAsync(() => !Shows(agent, "42%"), "the bar dropped", window);
            await Assert.That(agent.IsOpen).IsTrue();
            await Assert.That(NamedRow(agent, "d1")).IsNotNull();

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// Use default answers an account refusal only: a launch failure leaves it hidden, a refused
    /// account shows it beside the named message, and pressing it takes the default without launching.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Use_default_shows_in_the_error_banner_only_for_an_account_refusal() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new AccountRig(Tmp.PathTo("app-state.json"));
            await rig.ResolveAsync();
            var vm = rig.Vm;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 700 };
            window.Show();
            Settle(window);

            var banner = Find<Border>(window, "StartErrorBanner")!;
            var useDefault = Find<Button>(window, "UseDefaultAccountButton")!;
            var chip = Find<TextBlock>(window, "AgentChipLabel")!;

            rig.Launch.Next = new LaunchOutcome(false, null, "Daemon 'kcap-dev' is at capacity.");
            await vm.StartCommand.Execute();
            Settle(window);
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(useDefault.IsVisible).IsFalse();

            await vm.PickAccountAsync("a2");
            Settle(window);
            await Assert.That(chip.Text).IsEqualTo("Claude Code · Default · work");

            rig.Snapshots.OnNext(AccountsSnapshot(2, HomeAccount));
            await vm.StartCommand.Execute();
            Settle(window);
            await Assert.That(Find<TextBlock>(window, "StartErrorText")!.Text)
                .IsEqualTo("work is not available on this machine. Use the default account or pick another.");
            await Assert.That(useDefault.IsVisible).IsTrue();
            await Assert.That(chip.Text).IsEqualTo("Claude Code · Default · work — unavailable");
            var starts = rig.Launch.StartCount;

            Click(window, useDefault);
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(useDefault.IsVisible).IsFalse();
            await Assert.That(chip.Text).IsEqualTo("Claude Code · Default");
            await Assert.That(rig.Launch.StartCount).IsEqualTo(starts);

            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// The open panel follows the view-model: the pending line and its Retry stand in for the rows
    /// until the listing and snapshot answer, and the rows then replace them without a reopen.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_panel_shows_the_rows_once_the_listing_and_snapshot_answer() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new AccountRig(Tmp.PathTo("app-state.json"));
            await rig.Vm.SelectRepositoryAsync("/repos/kcap-cli");
            rig.Remote.ListingSubject.OnNext(new DaemonListing(null, 1, true));
            await UntilAsync(() => rig.Vm.AccountRetryVisible, "the failed listing");
            var view = new LauncherPaneView { DataContext = rig.Vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            Settle(window);

            var (agent, _) = OpenMenu(window, view, "AgentChip");
            Settle(window);
            static bool Pending(Flyout menu) => ((Control)menu.Content!).GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text == "Loading accounts…");
            static Button? Retry(Flyout menu) => PanelButtons(menu).SingleOrDefault(b => b.Content as string == "Retry");
            await Assert.That(AccountHeader(agent)).IsNotNull();
            await Assert.That(Pending(agent)).IsTrue();
            await Assert.That(Retry(agent)).IsNotNull();
            await Assert.That(NamedRow(agent, "work")).IsNull();

            rig.AnswerListing();
            rig.Snapshots.OnNext(AccountsSnapshot(1, HomeAccount, WorkAccount));
            await UntilAsync(() => NamedRow(agent, "work") is not null, "the account rows", window);

            await Assert.That(agent.IsOpen).IsTrue();
            await Assert.That(NamedRow(agent, "home, selected")).IsNotNull();
            await Assert.That(Pending(agent)).IsFalse();
            await Assert.That(Retry(agent)).IsNull();

            agent.Hide();
            Settle(window);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// Real pointer input on a control inside the open panel, which lives in its own popup root.
    static void ClickInPanel(Window window, Control target) {
        Settle(window);
        var root = TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException("Click target is not shown.");
        var origin = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)
            ?? throw new InvalidOperationException("Click target is not under its root.");
        root.MouseDown(origin, MouseButton.Left);
        root.MouseUp(origin, MouseButton.Left);
        Settle(window);
    }

    /// The rows belong to the selected vendor, so browsing another vendor's tab shows that vendor's
    /// models without them, and coming back shows them again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_account_section_shows_only_under_the_selected_vendors_tab() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new AccountRig(Tmp.PathTo("app-state.json"));
            await rig.ResolveAsync();
            var view = new LauncherPaneView { DataContext = rig.Vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            Settle(window);

            var (agent, _) = OpenMenu(window, view, "AgentChip");
            Settle(window);
            Button Tab(string label) => PanelButtons(agent).Single(b => b.Width == 34 && AutomationProperties.GetName(b) == label);
            await Assert.That(AccountHeader(agent)).IsNotNull();

            ClickInPanel(window, Tab("Codex"));
            await Assert.That(AccountHeader(agent)).IsNull();
            await Assert.That(NamedRow(agent, "work")).IsNull();
            await Assert.That(NamedRow(agent, "GPT-5.6, Codex")).IsNotNull();

            ClickInPanel(window, Tab("Claude Code"));
            await Assert.That(AccountHeader(agent)).IsNotNull();
            await Assert.That(NamedRow(agent, "work")).IsNotNull();

            agent.Hide();
            Settle(window);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }

    /// The listing and snapshot re-emit on every server change notification; one that changes
    /// nothing the panel draws must leave its rows, and the focus on them, in place.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unchanged_listing_and_snapshot_leave_the_open_panel_rows_in_place() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new AccountRig(Tmp.PathTo("app-state.json"));
            await rig.ResolveAsync();
            var view = new LauncherPaneView { DataContext = rig.Vm };
            var window = new Window { Content = view, Width = 900, Height = 700 };
            window.Show();
            Settle(window);

            var (agent, _) = OpenMenu(window, view, "AgentChip");
            Settle(window);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Settle(window);
            var home = NamedRow(agent, "home, selected")!;
            var modelDefault = NamedRow(agent, "Default — Claude Code chooses, Claude Code, selected")!;
            await Assert.That(home.IsFocused).IsTrue();

            rig.AnswerListing();
            rig.Snapshots.OnNext(AccountsSnapshot(1, HomeAccount, WorkAccount));
            Settle(window);

            await Assert.That(NamedRow(agent, "home, selected")).IsSameReferenceAs(home);
            await Assert.That(NamedRow(agent, "Default — Claude Code chooses, Claude Code, selected")).IsSameReferenceAs(modelDefault);
            await Assert.That(home.IsFocused).IsTrue();

            agent.Hide();
            Settle(window);
            window.Close();
            Dispatcher.UIThread.RunJobs();
        });
    }
}

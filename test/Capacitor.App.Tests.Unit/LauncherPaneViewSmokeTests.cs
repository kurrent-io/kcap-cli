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
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the launcher pane. LauncherPaneView is a UserControl,
/// not a Window (unlike MainWindow) — each test hosts it inside a plain Window purely to give
/// headless something to Show(); session setup and control lookup otherwise copy
/// MainWindowSmokeTests exactly (see that file's own header comment).
public class LauncherPaneViewSmokeTests {
    [TempDir] public required TempDir Tmp { get; init; }

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
            var busy = Find<ProgressBar>(window, "BannerBusyBar")!;
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
        await Assert.That(noticeText).IsEqualTo(HomeViewModel.ServerLostNotice);
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
                var bar = Find<ProgressBar>(window, "BannerBusyBar")!;
                return (notice.Text, signInBtn.IsVisible, bar.IsVisible);
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

        await Assert.That(text).IsEqualTo(HomeViewModel.FinishingSignInNotice);
        await Assert.That(signIn).IsFalse();
        await Assert.That(busy).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Connection_banner_overlays_without_shifting_the_composer() {
        var (yBefore, yAfter, bannerVisible, bannerAbove) = await AvaloniaSession.DispatchAsync(() => {
            var (vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var headline = Find<TextBlock>(window, "LauncherHeadline")!;
            double Y(Visual c) => c.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var before = Y(headline);

            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(connection: "disconnected"));
            Dispatcher.UIThread.RunJobs();

            var banner = Find<Border>(window, "ConnectionBanner")!;
            var after = Y(headline);
            var bannerAbove = Y(banner) < after;
            var visible = banner.IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (before, after, visible, bannerAbove);
        });

        await Assert.That(bannerVisible).IsTrue();
        await Assert.That(yAfter).IsEqualTo(yBefore);
        await Assert.That(bannerAbove).IsTrue();
    }

    /// The progress track and the action cluster stay out of the layout until they have something
    /// to show. Actions sit below the message with a clear gap, even in a wide card.
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
            var bar = Find<ProgressBar>(window, "BannerBusyBar")!;
            var actions = Find<StackPanel>(window, "BannerActions")!;

            static double Top(Control c, Visual rel) => c.TranslatePoint(new Point(0, 0), rel)!.Value.Y;
            static double Left(Control c, Visual rel) => c.TranslatePoint(new Point(0, 0), rel)!.Value.X;
            static double Bottom(Control c, Visual rel) => Top(c, rel) + c.Bounds.Height;

            var gap = Top(button, banner) - Bottom(text, banner);
            var idleShot = (
                BarVisible: bar.IsVisible,
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
                BarVisible: bar.IsVisible,
                ActionsVisible: actions.IsEffectivelyVisible,
                BarBelow: Top(bar, banner) >= Bottom(text, banner),
                Compact: banner.Bounds.Height <= text.Bounds.Height + bar.Bounds.Height + 34);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (idleShot, busyShot);
        });

        await Assert.That(idle.BarVisible).IsFalse();
        await Assert.That(idle.TextTop).IsLessThan(22);
        await Assert.That(idle.AlignedLeft).IsTrue();
        await Assert.That(idle.Primary).IsTrue();
        await Assert.That(idle.Gap).IsGreaterThanOrEqualTo(13);
        await Assert.That(idle.Gap).IsLessThanOrEqualTo(15);
        await Assert.That(busy.BarVisible).IsTrue();
        await Assert.That(busy.ActionsVisible).IsFalse();
        await Assert.That(busy.BarBelow).IsTrue();
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
            var below = detail is not null && Top(detail, window) >= Top(copy, window) + copy.Bounds.Height + 5;
            var above = detail is not null && Top(button, window) >= Top(detail, window) + detail.Bounds.Height + 13;

            message.OnNext(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var cleared = detail is not null && !detail.IsVisible
                && Math.Abs(Top(button, window) - Top(copy, window) - copy.Bounds.Height - 14) < 1;
            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return (headline, detailText, below, above, cleared);
        });
        await Assert.That(headline).IsEqualTo(HomeViewModel.DaemonDownNotice);
        await Assert.That(detailText).IsEqualTo(error);
        await Assert.That(belowCopy).IsTrue();
        await Assert.That(aboveActions).IsTrue();
        await Assert.That(cleared).IsTrue();
    }

    /// A narrow card gives the copy the full width and puts the action below, aligned left.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Connection_banner_keeps_its_action_inside_a_narrow_card() {
        var (inside, below, alignedLeft, iconAligned, wrapped) = await AvaloniaSession.DispatchAsync(() => {
            var (vm, service, _, tmp) = Build();
            using var _tmp = tmp;
            vm.AttachDaemonRecovery(
                ReactiveCommand.Create(() => { }),
                ReactiveCommand.Create(() => { }),
                new BehaviorSubject<bool>(true), new BehaviorSubject<bool>(false), Observable.Return<string?>(null));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            var window = new Window { Content = new LauncherPaneView { DataContext = vm }, Width = 480, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var banner = Find<Border>(window, "ConnectionBanner")!;
            var text = Find<TextBlock>(window, "BannerMessageText")!;
            var button = Find<Button>(window, "StartDaemonButton")!;
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), banner)!.Value.X;
            var textTop = text.TranslatePoint(new Point(0, 0), banner)!.Value.Y;
            var buttonTop = button.TranslatePoint(new Point(0, 0), banner)!.Value.Y;
            var icon = text.FindAncestorOfType<Grid>()!.Children.OfType<AppIcon>().Single();
            var iconTop = icon.TranslatePoint(new Point(0, 0), banner)!.Value.Y;
            var textLeft = text.TranslatePoint(new Point(0, 0), banner)!.Value.X;
            var buttonLeft = button.TranslatePoint(new Point(0, 0), banner)!.Value.X;
            var shot = (
                Inside: right <= banner.Bounds.Width + 1,
                Below: buttonTop >= textTop + text.Bounds.Height + 7,
                AlignedLeft: Math.Abs(buttonLeft - textLeft) < 1,
                IconAligned: Math.Abs(iconTop + icon.Bounds.Height / 2 - (textTop + 9)) < 1,
                Wrapped: text.Bounds.Height > 18);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            vm.Dispose();
            return shot;
        });

        await Assert.That(inside).IsTrue();
        await Assert.That(below).IsTrue();
        await Assert.That(alignedLeft).IsTrue();
        await Assert.That(iconAligned).IsTrue();
        await Assert.That(wrapped).IsTrue();
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
}

using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using SvcSystems.UI.Terminal;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering of MainWindow against a fake Connected snapshot: the rail footer shows
/// connection and tenant on one line, then the daemon's name, version and agent count.
public class MainWindowSmokeTests {
    sealed class NeverLaunchClient : ILaunchClient {
        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) =>
            Task.FromResult(new LaunchOutcome(false, null, "unexpected launch"));
    }

    // Real AppNotifier (not RecordingNotifier) — the production notifier is fine here; most of
    // these tests don't exercise the toast overlay at all (window.Notifier is left unset), and
    // the one that does (below) needs a real IObservable<string> to subscribe through.
    static (AgentActionService Actions, IAppNotifier Notifier) NewActions(FakeDaemonClientService service) {
        var notifier = new AppNotifier();
        var actions = new AgentActionService(new ScriptedLocalControlOps(), notifier, new RecordingOpener(), service.SnapshotsSubject, CancellationToken.None, NeverConfirm.Confirm);
        return (actions, notifier);
    }

    /// A real WorkspaceViewModel over the fake service and scripted attach/surface fakes — same
    /// pieces MainWindowViewModelTests wires, over the actions this file's NewActions built.
    static WorkspaceViewModel NewWorkspace(FakeDaemonClientService service, AgentActionService actions, string agentId) =>
        new(agentId, service, actions, new FakeTerminalAttachClientFactory().Factory,
            () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(),
            new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader());

    // A tip's bindings only resolve once it is parented to its adorner. String tips need no
    // open; two-line panels do. Always open so callers can treat both the same.
    static string[] VisibleTipLines(Control control) {
        ToolTip.SetIsOpen(control, true);
        Dispatcher.UIThread.RunJobs();
        var tip = ToolTip.GetTip(control);
        var lines = tip as string is { } text
            ? new[] { text }
            : (tip as StackPanel)!.Children.OfType<TextBlock>()
                .Where(t => t.IsVisible)
                .Select(t => t.Text ?? "")
                .ToArray();
        ToolTip.SetIsOpen(control, false);
        return lines;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task MainWindow_renders_compact_footer_with_server_daemon_and_version() {
        // Rendered text, so no immediate scheduler: an OAPH delivered immediately notifies
        // before its value is readable, and a binding that reads on the notification keeps the
        // stale one. The dispatcher scheduler sets the value first, as it does in the app.
        var rendered = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap(
                daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999",
                connection: "connected", active: 1, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent");
            var window = new MainWindow { DataContext = vm };
            window.Show();
            // Control.Loaded is POSTED at DispatcherPriority.Loaded (Avalonia defers it, it
            // never fires synchronously from Show()) — pump the dispatcher so it actually
            // runs before reading bound text. This is what drives ReactiveWindow<T>'s
            // built-in Loaded->ViewModel.Activator.Activate() wiring.
            Dispatcher.UIThread.RunJobs();

            var texts = string.Join('\n', window.GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(t => t.Text is { Length: > 0 } text
                    ? text
                    : string.Concat((t.Inlines ?? []).OfType<Run>().Select(r => r.Text ?? ""))));

            window.Close();
            Dispatcher.UIThread.RunJobs(); // flush the deferred Unloaded post so the VM's WhenActivated-scoped subscriptions actually get disposed before the next test runs

            return texts;
        });

        await Assert.That(rendered).Contains("Connected");
        await Assert.That(rendered).Contains("1 of 5 agents");
        await Assert.That(rendered).DoesNotContain("http://localhost:9999");
        await Assert.That(rendered).Contains("kurrent");
        await Assert.That(rendered).Contains("daemon-a");
        await Assert.That(rendered).Contains("1.2.3");
    }

    /// ReactiveCommand does NOT reschedule a SUPPLIED canExecute onto its outputScheduler (only
    /// IsExecuting/ThrownExceptions ride it), so canStart/canRetry built off service.Status without
    /// an ObserveOn would carry a background-thread Status event's CanExecuteChanged, and a bound
    /// Button's IsEnabled write, onto that thread, tripping Avalonia's thread-affinity check.
    ///
    /// Deliberately NOT wrapped in AvaloniaSession.WithImmediateRxScheduler: that swaps
    /// RxSchedulers.MainThreadScheduler for ImmediateScheduler.Instance, which would deliver the
    /// background-thread OnNext synchronously on the CALLING (background) thread regardless of
    /// whether an ObserveOn is present, so it could never catch a missing one. This test
    /// needs the REAL Avalonia-dispatcher scheduler that UseReactiveUI() installs for the whole
    /// headless session, so a background-thread publish actually has to cross a real dispatcher
    /// boundary to reach the Button.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Status_transition_from_a_background_thread_does_not_throw_and_converges() {
        var (thrown, startEnabledAfter) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Exception? caught = null;
            var backgroundPublish = Task.Run(() => {
                try {
                    service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
                } catch (Exception ex) {
                    caught = ex;
                }
            });
            backgroundPublish.Wait(TimeSpan.FromSeconds(5));

            // Give a correctly-marshaled dispatcher post a chance to actually run and converge.
            Dispatcher.UIThread.RunJobs();

            var startButton = window.GetVisualDescendants().OfType<Button>()
                .First(b => Equals(b.Content, "Start daemon"));
            var enabled = startButton.IsEnabled;

            window.Close();
            return (caught, enabled);
        });

        await Assert.That(thrown).IsNull();
        await Assert.That(startEnabledAfter).IsTrue();
    }

    /// DaemonClientService.StartDaemonAsync deliberately rethrows OperationCanceledException when
    /// the caller-supplied ct fires mid-wait (App's `_shutdown` token: ct abandons the WAIT, not the
    /// started daemon), and App.OnShutdownRequested cancels that token on Cmd+Q while a start may
    /// be in flight. Nothing subscribes to StartDaemonCommand.ThrownExceptions, so an uncaught OCE
    /// in RunStartAsync would reach ReactiveUI.RxState.DefaultExceptionHandler, which reschedules
    /// an UnhandledErrorException onto the still-alive dispatcher and crashes the app.
    ///
    /// Deliberately NOT wrapped in WithImmediateRxScheduler, for the same reason as the sibling
    /// test above: only a REAL dispatcher round-trip (via Dispatcher.UIThread.RunJobs(), which
    /// drains Avalonia's dispatcher queue including jobs enqueued mid-drain, and re-throws an
    /// unhandled job exception out of the call since nothing subscribes to
    /// Dispatcher.UIThread.UnhandledException) reproduces a scheduler-rescheduled exception.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Quit_during_start_does_not_crash() {
        var (thrown, completed) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));

            var shutdown = new CancellationTokenSource();
            var (actions, _) = NewActions(service);
            var vm = new MainWindowViewModel(service, shutdown.Token, TestActivity.New(), TimeProvider.System);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            service.StartBehavior = async ct => {
                // Blocks until ct fires, then throws OCE — mirrors StartDaemonAsync's real
                // ct-abandons-the-wait contract (e.g. its own process.WaitForExitAsync(ct)).
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new StartDaemonResult(true, null); // unreachable
            };

            var executeTask = vm.StartDaemonCommand.Execute().ToTask();

            Exception? caught = null;
            try {
                shutdown.Cancel(); // simulates Cmd+Q mid-start: OnShutdownRequested cancels this same token

                // The ct-cancellation continuation (and any exception ReactiveCommand reschedules
                // as a result) may hop through a thread-pool continuation before landing back on
                // the dispatcher queue, so poll rather than assume one RunJobs() drains it all.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (!executeTask.IsCompleted && DateTime.UtcNow < deadline) {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }
            } catch (Exception ex) {
                caught = ex;
            }

            var isCompleted = executeTask.IsCompleted;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (caught, isCompleted);
        });

        await Assert.That(thrown).IsNull();
        await Assert.That(completed).IsTrue();
    }

    /// BannerMessage must not reserve dead space when empty: Connecting… shows a notice, then a
    /// failed start replaces that body with the start message (one line, never stacked).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task StartMessage_and_reason_text_collapse_when_empty_and_appear_once_set() {
        var (bannerInitially, bannerTextUnreachable, bannerTextAfterFailure) =
            await AvaloniaSession.DispatchAsync(async () => {
                using var tmp = TempDir.WithPathTo("app-state.json", out var path);
                var service = new FakeDaemonClientService();
                var home = new HomeViewModel(
                    service, new AppStateStore(path), new NeverLaunchClient(),
                    () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
                var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, home: home);
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                TextBlock Banner() =>
                    window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "BannerMessageText");
                Border NoticeBanner() => Banner().FindAncestorOfType<Border>()!;

                var bannerInit = NoticeBanner().IsVisible;

                service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
                Dispatcher.UIThread.RunJobs();
                var textUnreachable = Banner().Text;

                service.StartBehavior = _ => Task.FromResult(new StartDaemonResult(false, "boom: could not bind socket"));
                await vm.StartDaemonCommand.Execute().ToTask();
                Dispatcher.UIThread.RunJobs();
                var textAfter = Banner().Text;

                window.Close();
                Dispatcher.UIThread.RunJobs();
                home.Dispose();

                return (bannerInit, textUnreachable, textAfter);
            });

        await Assert.That(bannerInitially).IsTrue(); // Connecting… still has a notice
        await Assert.That(bannerTextUnreachable).IsEqualTo(HomeViewModel.DaemonDownNotice);
        await Assert.That(bannerTextAfterFailure).IsEqualTo("boom: could not bind socket");
    }

    // ---- Toast overlay (WindowNotificationManager) ----
    //
    // Proves the real production wiring end to end: MainWindow.Notifier assigned exactly as
    // App.BuildAndShowMainWindow does, a WindowNotificationManager actually constructible and
    // installable under the headless session, and the fired message rendered as visible text.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Toast_renders_the_notifier_message() {
        var rendered = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var (actions, notifier) = NewActions(service);
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
            var window = new MainWindow { DataContext = vm, Notifier = notifier };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            notifier.Notify("Couldn't stop agent-a");
            Dispatcher.UIThread.RunJobs();

            var texts = string.Join('\n', window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? ""));

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return texts;
        });

        await Assert.That(rendered).Contains("Kurrent Capacitor");
        await Assert.That(rendered).Contains("Couldn't stop agent-a");
    }

    // ---- Activity gate ----
    //
    // Proves the real production wiring end to end — the Activity flyout's open state, the
    // launcher pane being on screen (NO workspace open), and the window's
    // own IsVisible (Show()/Hide()) all drive ActivityViewModel.OnTabVisibleChanged through the
    // code-behind, not just that the ViewModel reacts correctly in isolation
    // (ActivityViewModelTests already covers that). Each gate flips the polling off on its own;
    // each TRUE transition issues exactly one more immediate read, awaited via
    // PendingRefreshForTesting — the VM's stat+read hops off the UI thread, so RunJobs() alone no
    // longer guarantees the read has landed. Opening a workspace under the popup CLOSES the
    // flyout, so coming back does not auto-resume — the feed reopens by click.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Activity_polls_only_while_open_on_the_launcher_pane_in_a_visible_window() {
        var reads = await AvaloniaSession.DispatchAsync(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var reader = new ScriptedReader();
            reader.Set(new ConsentLogReadResult([], true));
            var activity = new ActivityViewModel(reader.Read, () => "k", new FakeTicker());
            var attach = new FakeTerminalAttachClientFactory();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, activity, TimeProvider.System,
                workspaceFactory: agentId => new WorkspaceViewModel(
                    agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(),
                    new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader()));
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ActivityButton");
            var flyout = button.Flyout!;
            var closed = reader.ReadCalls; // starts closed — no read

            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var opened = reader.ReadCalls;

            vm.OpenSession("0123456789abcdef0123456789abcdef"); // workspace replaces the launcher
            Dispatcher.UIThread.RunJobs();
            var workspaceOpen = reader.ReadCalls;

            vm.CloseWorkspace();
            Dispatcher.UIThread.RunJobs();
            var launcherBack = reader.ReadCalls; // closed by the swap — still off

            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var afterReopen = reader.ReadCalls;

            window.Hide();
            Dispatcher.UIThread.RunJobs();
            var afterHide = reader.ReadCalls;

            // Whether the popup survived the window hide is platform detail: Show() alone resumes
            // a surviving flyout, ShowAt() reopens a closed one, and a repeated true is not a
            // transition — either way exactly one more read lands.
            window.Show();
            Dispatcher.UIThread.RunJobs();
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            await activity.PendingRefreshForTesting!;
            var afterReshow = reader.ReadCalls;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (closed, opened, workspaceOpen, launcherBack, afterReopen, afterHide, afterReshow);
        });

        await Assert.That(reads.closed).IsEqualTo(0);
        await Assert.That(reads.opened).IsEqualTo(1); // opening on the launcher: one immediate read
        await Assert.That(reads.workspaceOpen).IsEqualTo(1); // a workspace opening is a FALSE transition
        await Assert.That(reads.launcherBack).IsEqualTo(1);
        await Assert.That(reads.afterReopen).IsEqualTo(2);
        await Assert.That(reads.afterHide).IsEqualTo(2); // hiding is a FALSE transition
        await Assert.That(reads.afterReshow).IsEqualTo(3);
    }

    /// PR context follows the window being on screen: an open workspace in a visible window loads
    /// and shows its pull request while another app holds keyboard focus, and only minimizing or
    /// hiding the window masks it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pull_request_context_follows_the_window_being_on_screen_not_keyboard_focus() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var time = new FakeTimeProvider();
            var source = new FakePullRequestSource(time);
            var attach = new FakeTerminalAttachClientFactory();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                workspaceFactory: agentId => new WorkspaceViewModel(
                    agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), time, new RecordingOpener(),
                    new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: source));
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try {
                vm.OpenSession("agent");
                Dispatcher.UIThread.RunJobs();
                var workspace = (WorkspaceViewModel)vm.CurrentWorkspace!;
                var pullRequests = workspace.PullRequests!;
                service.Agents.AddOrUpdate(WorkspaceFixtures.Agent("agent", "claude", hasTerminal: false, sessionId: "session"));
                await (workspace.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
                await WorkspaceFixtures.WaitUntilAsync(() => pullRequests.CanReveal, what: "PR shown in the visible window");

                await Assert.That(window.IsActive).IsTrue();
                AvaloniaSession.LoseKeyboardFocus(window);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(window.IsActive).IsFalse();
                await Assert.That(pullRequests.CanReveal).IsTrue();

                window.WindowState = WindowState.Minimized;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(pullRequests.CanReveal).IsFalse();

                window.WindowState = WindowState.Normal;
                Dispatcher.UIThread.RunJobs();
                await WorkspaceFixtures.WaitUntilAsync(() => pullRequests.CanReveal, what: "PR shown again after restoring");

                window.Hide();
                Dispatcher.UIThread.RunJobs();
                await Assert.That(pullRequests.CanReveal).IsFalse();
            } finally {
                vm.CloseWorkspace();
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// The surface swap itself: the XAML side of what WorkspaceNavigationTests pins on
    /// the ViewModel. WorkspaceView is materialized from a template rather than always present, so
    /// this also proves the terminal control is CONSTRUCTED only once a workspace exists; closing
    /// it lands on the Sessions surface's placeholder, never back on Home.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_a_session_swaps_the_launcher_pane_for_the_workspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var swap = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var (actions, _) = NewActions(service);
                var attach = new FakeTerminalAttachClientFactory();
                var vm = new MainWindowViewModel(
                    service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                    workspaceFactory: agentId => new WorkspaceViewModel(
                        agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(),
                        new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader()));
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Control Surface(string name) =>
                    window.GetVisualDescendants().OfType<Control>().First(c => c.Name == name);

                var launcherAtBoot = Surface("LauncherPane").IsVisible;
                var workspacesBefore = window.GetVisualDescendants().OfType<WorkspaceView>().Count();

                vm.OpenSession("0123456789abcdef0123456789abcdef");
                Dispatcher.UIThread.RunJobs();
                var launcherGone = Surface("LauncherPane").IsVisible;
                var opened = window.GetVisualDescendants().OfType<WorkspaceView>().ToList();
                // Read NOW, not in the return tuple: closing below detaches the view and clears the
                // very DataContext this is asserting on.
                var boundToWorkspace = opened.FirstOrDefault()?.DataContext is WorkspaceViewModel;

                vm.CloseWorkspace();
                Dispatcher.UIThread.RunJobs();
                var launcherBack = Surface("LauncherPane").IsVisible;
                var workspacesAfter = window.GetVisualDescendants().OfType<WorkspaceView>().Count();

                window.Close();
                Dispatcher.UIThread.RunJobs();

                return (launcherAtBoot, workspacesBefore, launcherGone,
                    OpenedCount: opened.Count, boundToWorkspace, launcherBack, workspacesAfter);
            });

            await Assert.That(swap.launcherAtBoot).IsTrue(); // the empty state IS the launcher
            await Assert.That(swap.workspacesBefore).IsEqualTo(0); // nothing terminal-shaped until a session is opened
            await Assert.That(swap.launcherGone).IsFalse();
            await Assert.That(swap.OpenedCount).IsEqualTo(1);
            await Assert.That(swap.boundToWorkspace).IsTrue();
            await Assert.That(swap.launcherBack).IsTrue();
            await Assert.That(swap.workspacesAfter).IsEqualTo(0);
        });
    }

    /// A shown MainWindow whose rail holds two rows, "Fix the flaky test"
    /// and "Leave this one alone", under one worktree named feature-x. A sibling worktree
    /// adds one more row under that name.
    static (MainWindowViewModel Vm, MainWindow Window) RailWindow(
            bool awaitingInput = false, int? liveSubagents = null, string? model = null, string? siblingWorktree = null,
            string? firstStatus = null, IReadOnlySet<string>? pendingIds = null, PendingLaunchDto? pending = null) {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        if (pending is not null) service.Pending.AddOrUpdate(pending);
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a1", "agent", "claude", "/dev/alpha/wt/feature-x", firstStatus ?? "Running",
            null, null, null, DateTime.UtcNow, model, null, Title: "Fix the flaky test",
            AwaitingInput: awaitingInput ? true : null, LiveSubagents: liveSubagents));
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a2", "agent", "claude", "/dev/alpha/wt/feature-x", "Running",
            null, null, null, DateTime.UtcNow, null, null, Title: "Leave this one alone"));
        if (siblingWorktree is not null)
            service.Agents.AddOrUpdate(new AgentStatusDto(
                "a3", "agent", "claude", $"/dev/alpha/wt/{siblingWorktree}", "Running",
                null, null, null, DateTime.UtcNow, null, null, Title: "Sibling work",
                AwaitingInput: awaitingInput ? true : null));

        var (actions, _) = NewActions(service);
        MainWindowViewModel? vm = null;
        Func<string, string> resolveRepoRoot = p => p.Contains("/wt/", StringComparison.Ordinal)
            ? p[..p.IndexOf("/wt/", StringComparison.Ordinal)]
            : p;
        var directory = new AgentDirectory(
            service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
            resolveRepoRoot, null, null, TimeProvider.System);
        var rail = new SessionRailViewModel(
            directory, id => vm!.OpenSession(id), _ => { }, TimeProvider.System, resolveRepoRoot,
            agentsWithPending: pendingIds is null ? null : Observable.Return(pendingIds));
        vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            workspaceFactory: id => NewWorkspace(service, actions, id), rail: rail);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (vm, window);
    }

    static Button RailRow(MainWindow window, string text) => window.GetVisualDescendants().OfType<Button>()
        .First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));

    /// The rail's own click path: a session row rendered by SessionRailView carries the
    /// VM's OpenCommand, and executing it opens that agent's workspace.
    ///
    /// Also pins the selection highlight as RENDERED state, not just as a bound class. A row's
    /// resting Background must come from the `railRow` class style: a local `Background` attribute
    /// on the Button would be a LocalValue, outrank the `.selected`/`.holdsSelected` style
    /// triggers, and leave the highlight permanently invisible while still passing any
    /// class-membership assertion. Comparing the opened row's alpha against its sibling's is what
    /// fails if a future local value defeats the style again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_click_opens_the_workspace_and_highlights_the_open_row() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var opened = await AvaloniaSession.DispatchAsync(() => {
                var (vm, window) = RailWindow();
                Button Row(string text) => RailRow(window, text);
                byte Alpha(Button b) => (b.Background as ISolidColorBrush)?.Color.A ?? 0;

                Row("Fix the flaky test").Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                var result = (vm.CurrentWorkspace?.AgentId,
                    SelectedClass: Row("Fix the flaky test").Classes.Contains("selected"),
                    SelectedAlpha: Alpha(Row("Fix the flaky test")),
                    SiblingAlpha: Alpha(Row("Leave this one alone")),
                    // The worktree header row carries the same hazard through holdsSelected.
                    WorktreeAlpha: Alpha(Row("feature-x")));
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(opened.AgentId).IsEqualTo("a1");
            await Assert.That(opened.SelectedClass).IsTrue();
            await Assert.That(opened.SelectedAlpha).IsGreaterThan((byte)0); // the highlight actually paints
            await Assert.That(opened.SiblingAlpha).IsEqualTo((byte)0); // an unopened row stays transparent
            await Assert.That(opened.WorktreeAlpha).IsGreaterThan((byte)0);
        });
    }

    /// A parent that stopped to wait for the user while its subagents run keeps the pulsing dot
    /// visible beside the attention badge — the one state the pulse exists to show.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_dot_stays_visible_beside_the_wait_badge_while_subagents_run() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (visible, pulses) = await AvaloniaSession.DispatchAsync(() => {
                var (_, window) = RailWindow(awaitingInput: true, liveSubagents: 2);
                var row = RailRow(window, "Fix the flaky test");
                var dot = row.GetVisualDescendants().OfType<Ellipse>().First();
                var result = (dot.IsVisible, row.GetVisualDescendants().OfType<Visual>().Any(v => PulseClock.GetIsActive(v) && v.IsEffectivelyVisible));
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(visible).IsTrue();
            await Assert.That(pulses).IsTrue();
        });
    }

    /// A selected row must read as selected next to a hovered one: hover paints the raised surface
    /// brush, so the selection needs its own background and a heavier title, not a leading edge
    /// that would inset that row past its siblings.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selected_row_is_distinct_from_a_hovered_row() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var seen = await AvaloniaSession.DispatchAsync(() => {
                var (_, window) = RailWindow();
                RailRow(window, "Fix the flaky test").Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();

                var selected = RailRow(window, "Fix the flaky test");
                var sibling = RailRow(window, "Leave this one alone");
                var hover = ((ISolidColorBrush)Avalonia.Application.Current!.FindResource("KcapSurfaceRaisedBrush")!).Color;
                static TextBlock Title(Button row) => row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("rowTitle"));
                var result = (
                    SelectedBackground: (selected.Background as ISolidColorBrush)?.Color,
                    Hover: hover,
                    SelectedEdge: selected.BorderThickness.Left,
                    SiblingEdge: sibling.BorderThickness.Left,
                    SelectedWeight: Title(selected).FontWeight,
                    SiblingWeight: Title(sibling).FontWeight,
                    TitleTip: ToolTip.GetTip(Title(selected)));
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(seen.SelectedBackground).IsNotNull();
            await Assert.That(seen.SelectedBackground).IsNotEqualTo(seen.Hover);
            await Assert.That(seen.SelectedEdge).IsEqualTo(0);
            await Assert.That(seen.SiblingEdge).IsEqualTo(0);
            await Assert.That(seen.SelectedWeight).IsEqualTo(FontWeight.SemiBold);
            await Assert.That(seen.SiblingWeight).IsEqualTo(FontWeight.Normal);
            await Assert.That(seen.TitleTip).IsNull();
        });
    }

    /// The worktree header is as tall as its attention badge while expanded, so collapsing onto
    /// the badge does not grow the row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Collapsing_a_waiting_worktree_does_not_grow_the_header() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var seen = await AvaloniaSession.DispatchAsync(() => {
                var (_, window) = RailWindow(awaitingInput: true);
                var header = RailRow(window, "feature-x");
                var expanded = header.Bounds.Height;
                header.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var collapsed = header.Bounds.Height;
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return (expanded, collapsed);
            });
            await Assert.That(seen.expanded).IsGreaterThanOrEqualTo(32);
            await Assert.That(seen.collapsed).IsEqualTo(seen.expanded);
        });
    }

    /// The count sits tight against the chevron on the right edge, expanded or collapsed.
    /// Collapsed, the status mark sits in a fixed column, so worktrees whose names differ in
    /// length show their statuses at one x.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Worktree_statuses_line_up_beside_a_tight_count() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var seen = await AvaloniaSession.DispatchAsync(() => {
                var (_, window) = RailWindow(awaitingInput: true, siblingWorktree: "x");
                window.UpdateLayout();
                var header = RailRow(window, "feature-x");
                var sibling = RailRow(window, "x");
                double Left(Control control) => control.TranslatePoint(default, window)!.Value.X;
                double Right(Control control) => control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
                Avalonia.Controls.Shapes.Path Chevron(Button row) => row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(p => p.IsVisible && p.StrokeThickness == 1.8);
                TextBlock Count(Button row, string text) => row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == text);
                AgentStatusMark Mark(Button row) => row.GetVisualDescendants().OfType<AgentStatusMark>().First(m => m.IsVisible);
                var label = header.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "feature-x");
                var branch = header.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().First(p => p.StrokeThickness == 1.5);
                var expandedGap = Left(Chevron(header)) - Right(Count(header, "2"));
                header.Command!.Execute(null);
                sibling.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var result = (
                    ExpandedGap: expandedGap,
                    CollapsedGap: Left(Chevron(header)) - Right(Count(header, "2")),
                    MarkOffset: Math.Abs(Left(Mark(header)) - Left(Mark(sibling))),
                    MarkClearsCount: Left(Count(header, "2")) - Right(Mark(header)),
                    ChevronOffset: Math.Abs(Left(Chevron(header)) - Left(Chevron(sibling))),
                    BranchBeforeTitle: Right(branch) <= Left(label),
                    Parenthetical: header.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "(2)"));
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(seen.ExpandedGap).IsGreaterThan(0);
            await Assert.That(seen.ExpandedGap).IsLessThan(8);
            await Assert.That(seen.CollapsedGap).IsGreaterThan(0);
            await Assert.That(seen.CollapsedGap).IsLessThan(8);
            await Assert.That(seen.MarkOffset).IsLessThan(0.5);
            await Assert.That(seen.MarkClearsCount).IsGreaterThan(0);
            await Assert.That(seen.ChevronOffset).IsLessThan(0.5);
            await Assert.That(seen.BranchBeforeTitle).IsTrue();
            await Assert.That(seen.Parenthetical).IsFalse();
        });
    }

    /// The vendor mark, the status word, and the age share one baseline. The age has no extra
    /// padding, or it sits below that line. The row carries the only tip, and it names the
    /// harness and the model; no child shows a tip of its own.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Vendor_mark_and_running_time_share_the_status_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var seen = await AvaloniaSession.DispatchAsync(() => {
                var (_, window) = RailWindow(model: "opus");
                window.UpdateLayout();
                var row = RailRow(window, "Fix the flaky test");
                var mark = row.GetVisualDescendants().OfType<AgentStatusMark>().First();
                var word = mark.FindControl<TextBlock>("StatusWord")!;
                var vendor = row.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("vendorMark"));
                var meta = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("railMeta"));
                double Center(Control control) =>
                    control.TranslatePoint(new Point(0, control.Bounds.Height / 2), row)!.Value.Y;
                double Baseline(TextBlock text) =>
                    text.TranslatePoint(new Point(0, text.TextLayout.Baseline), row)!.Value.Y;
                double Left(Control control) => control.TranslatePoint(default, row)!.Value.X;
                double Right(Control control) => control.TranslatePoint(new Point(control.Bounds.Width, 0), row)!.Value.X;
                var glyph = mark.FindControl<Panel>("Glyph")!;
                var result = (
                    Word: Center(word),
                    WordBaseline: Baseline(word),
                    MetaBaseline: Baseline(meta),
                    Vendor: Center(vendor),
                    Glyph: Center(glyph),
                    Meta: Center(meta),
                    VendorName: AutomationProperties.GetName(vendor),
                    VendorLeft: Left(vendor),
                    MarkLeft: Left(mark),
                    MetaGap: row.Bounds.Width - Right(meta),
                    ModelInRow: row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "opus"),
                    ChildTips: row.GetVisualDescendants().Count(v => v is Control c && ToolTip.GetTip(c) is not null),
                    RowTipIsStatus: ToolTip.GetTip(row) is AgentStatusTip,
                    RowTip: ((RailSessionViewModel)row.DataContext!).Tooltip,
                    HasMark: vendor.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Any(),
                    MetaAlign: meta.VerticalAlignment);
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(seen.VendorName).IsEqualTo("Claude Code");
            await Assert.That(seen.ChildTips).IsEqualTo(0);
            await Assert.That(seen.RowTipIsStatus).IsTrue();
            await Assert.That(seen.RowTip).Contains("Claude Code\nHarness");
            await Assert.That(seen.RowTip).Contains("opus\nModel");
            await Assert.That(seen.VendorLeft).IsLessThan(seen.MarkLeft);
            await Assert.That(seen.MetaGap).IsLessThan(16);
            await Assert.That(seen.ModelInRow).IsFalse();
            await Assert.That(seen.HasMark).IsTrue();
            await Assert.That(Math.Abs(seen.WordBaseline - seen.MetaBaseline)).IsLessThan(1);
            await Assert.That(Math.Abs(seen.Word - seen.Vendor)).IsLessThan(2);
            await Assert.That(Math.Abs(seen.Word - seen.Glyph)).IsLessThan(2);
            await Assert.That(Math.Abs(seen.Word - seen.Meta)).IsLessThan(2);
            await Assert.That(seen.MetaAlign).IsEqualTo(VerticalAlignment.Center);
        });
    }

    /// A raw daemon status and an unknown launch stage are open text. The row caps both so they
    /// stay inside the rail; "Needs you" and a known stage still fit whole. A trimmed stage is
    /// read in full from the row's tip, not one of its own.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_long_status_and_launch_stage_stay_inside_the_row() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            const string longStatus = "synchronizing_remote_workspace_credentials_before_the_session_can_start";
            const string longStage = "waiting_for_the_runtime_to_finish_its_handshake_and_publish_the_session";
            var seen = await AvaloniaSession.DispatchAsync(() => {
                var (_, window) = RailWindow(
                    firstStatus: longStatus,
                    pendingIds: new HashSet<string> { "a2" },
                    pending: new PendingLaunchDto("p1", "claude", "/dev/alpha", "Pending launch", DateTime.UtcNow, longStage));
                window.UpdateLayout();

                bool Trimmed(TextBlock block) => NaturalWidth(block) > block.Bounds.Width + 4;
                double Slack(TextBlock block) => block.Bounds.Width - NaturalWidth(block);
                double Right(Control control, Visual relative) =>
                    control.TranslatePoint(new Point(control.Bounds.Width, 0), relative)!.Value.X;
                double Left(Control control, Visual relative) => control.TranslatePoint(default, relative)!.Value.X;
                double CenterY(Control control, Visual relative) =>
                    control.TranslatePoint(new Point(0, control.Bounds.Height / 2), relative)!.Value.Y;

                var longRow = RailRow(window, "Fix the flaky test");
                var needsYouRow = RailRow(window, "Leave this one alone");
                var pendingRow = RailRow(window, "Pending launch");
                var longWord = longRow.GetVisualDescendants().OfType<AgentStatusMark>().First().FindControl<TextBlock>("StatusWord")!;
                var needsYouWord = needsYouRow.GetVisualDescendants().OfType<AgentStatusMark>().First().FindControl<TextBlock>("StatusWord")!;
                var pendingMeta = pendingRow.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("railMeta"));
                var pendingMark = pendingRow.GetVisualDescendants().OfType<AgentStatusMark>().First();
                var stageWidth = NaturalWidth(pendingMeta, LaunchStages.Label("session_created"));
                var result = (
                    LongTrimmed: Trimmed(longWord),
                    LongInside: Right(longWord, longRow) <= longRow.Bounds.Width + 1,
                    NeedsYou: needsYouWord.Text,
                    NeedsYouSlack: Slack(needsYouWord),
                    StageWidth: stageWidth,
                    MetaMax: pendingMeta.MaxWidth,
                    MetaText: pendingMeta.Text,
                    MetaTip: ToolTip.GetTip(pendingMeta),
                    RowTip: ((RailSessionViewModel)pendingRow.DataContext!).Tooltip,
                    MetaTrimmed: Trimmed(pendingMeta),
                    MetaInside: Right(pendingMeta, pendingRow) <= pendingRow.Bounds.Width + 1,
                    MarkClearsMeta: Math.Abs(CenterY(pendingMark, pendingRow) - CenterY(pendingMeta, pendingRow)) > 8
                        || Right(pendingMark, pendingRow) <= Left(pendingMeta, pendingRow) + 1);
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(seen.LongTrimmed).IsTrue();
            await Assert.That(seen.LongInside).IsTrue();
            await Assert.That(seen.NeedsYou).IsEqualTo("Needs you");
            await Assert.That(seen.NeedsYouSlack).IsGreaterThan(-4);
            await Assert.That(seen.StageWidth).IsLessThanOrEqualTo(seen.MetaMax);
            await Assert.That(seen.MetaText).IsEqualTo(LaunchStages.Label(longStage));
            await Assert.That(seen.MetaTip).IsNull();
            await Assert.That(seen.RowTip).Contains(seen.MetaText!);
            await Assert.That(seen.MetaTrimmed).IsTrue();
            await Assert.That(seen.MetaInside).IsTrue();
            await Assert.That(seen.MarkClearsMeta).IsTrue();
        });
    }

    static double NaturalWidth(TextBlock block, string? text = null) =>
        new FormattedText(
            text ?? block.Text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight), block.FontSize, null).Width;

    /// Cmd+N / Ctrl+N: the window binds the advertised New session shortcut to CloseWorkspaceCommand,
    /// which drops an open workspace back to the launcher (the new-session empty state).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task New_session_shortcut_is_bound_and_returns_to_the_launcher() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (hadWorkspace, hasMeta, hasCtrl, afterInvoke) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(
                    service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                    workspaceFactory: id => NewWorkspace(service, actions, id));
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm.OpenSession("0123456789abcdef0123456789abcdef");
                Dispatcher.UIThread.RunJobs();
                var had = vm.CurrentWorkspace is not null;

                bool Bound(KeyModifiers mod) => window.KeyBindings.Any(
                    k => k.Gesture is { Key: Key.N } g && g.KeyModifiers.HasFlag(mod));
                var meta = Bound(KeyModifiers.Meta);
                var ctrl = Bound(KeyModifiers.Control);

                window.KeyBindings.First(k => k.Gesture is { Key: Key.N } g && g.KeyModifiers.HasFlag(KeyModifiers.Meta))
                    .Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var after = vm.CurrentWorkspace;

                window.Close();
                Dispatcher.UIThread.RunJobs();
                return (had, meta, ctrl, after);
            });
            await Assert.That(hadWorkspace).IsTrue();
            await Assert.That(hasMeta).IsTrue();
            await Assert.That(hasCtrl).IsTrue();
            await Assert.That(afterInvoke).IsNull();
        });
    }

    /// Command+R runs the header refresh, including while the terminal has focus. Linux and Windows
    /// also bind Ctrl+R, except while the terminal has focus, where that key stays unhandled so the
    /// terminal keeps reverse-i-search. Ctrl+Shift+R refreshes there too. The menu stays Ctrl+R and
    /// stays enabled. The shortcut stays disabled on the launcher, before a session id, and while a
    /// refresh the user asked for is running. A poll does not count as that.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Refresh_shortcut_runs_the_open_works_refresh_and_follows_the_button() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            const string id = "0123456789abcdef0123456789abcdef";
            var service = new FakeDaemonClientService();
            var (actions, _) = NewActions(service);
            var source = new FakeWorkContextSource();
            var time = new FakeTimeProvider();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                workspaceFactory: agentId => new WorkspaceViewModel(
                    agentId, service, actions, new FakeTerminalAttachClientFactory().Factory,
                    () => new FakeTerminalSurface(), time, new RecordingOpener(), new FakePermissionService(),
                    source, new ScriptedLocalControlOps(), new NoAttachmentUploader()));
            var window = new MainWindow { DataContext = vm };
            var windowMenu = new AppMenuBar(new RecordingOpener(), () => [], () => null).Build(window)
                .Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window").Menu!;
            var refreshItem = windowMenu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Refresh");
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try {
                var bindings = window.KeyBindings.Where(k => k.Gesture is { Key: Key.R }).ToArray();
                bool Bound(KeyModifiers mod) => bindings.Any(k => k.Gesture!.KeyModifiers == mod);
                bool Can() => bindings.Single(k => k.Gesture!.KeyModifiers == KeyModifiers.Meta).Command!.CanExecute(null);
                var yield = new RefreshUnlessTerminalFocused(window);

                await Assert.That(Bound(KeyModifiers.Meta)).IsTrue();
                await Assert.That(Bound(KeyModifiers.Control)).IsEqualTo(RefreshShortcut.UsesControl);
                await Assert.That(Bound(KeyModifiers.Control | KeyModifiers.Shift)).IsEqualTo(RefreshShortcut.UsesControl);
                await Assert.That(bindings.Where(k => k.Gesture!.KeyModifiers != KeyModifiers.Control)
                    .All(k => ReferenceEquals(k.Command, vm.RefreshWorkCommand))).IsTrue();
                if (RefreshShortcut.UsesControl) {
                    await Assert.That(bindings.Single(k => k.Gesture!.KeyModifiers == KeyModifiers.Control).Command)
                        .IsNotSameReferenceAs(vm.RefreshWorkCommand);
                }
                await Assert.That(refreshItem.Command).IsSameReferenceAs(vm.RefreshWorkCommand);
                await Assert.That(refreshItem.Gesture).IsEqualTo(RefreshShortcut.Primary);
                await Assert.That(Can()).IsFalse();
                await Assert.That(refreshItem.IsEnabled).IsFalse();

                vm.OpenSession(id);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(Can()).IsFalse();

                service.Agents.AddOrUpdate(WorkspaceFixtures.Agent(id, "claude", true, "/repo/myproj", sessionId: id));
                var work = ((WorkspaceViewModel)vm.CurrentWorkspace!).WorkContext;
                await work.PendingReadForTesting!;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(work.HasSession).IsTrue();
                await Assert.That(work.IsRefreshing).IsFalse();
                await Assert.That(Can()).IsTrue();
                await Assert.That(refreshItem.IsEnabled).IsTrue();

                var tip = VisibleTipLines(window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "RefreshButton"));
                await Assert.That(tip).Contains(WorkContextViewModel.RefreshShortcutCaption);

                await Assert.That(yield.CanExecute(null)).IsTrue();
                var workspace = (WorkspaceViewModel)vm.CurrentWorkspace!;
                workspace.ShowTerminalCommand.Execute().Subscribe();
                Dispatcher.UIThread.RunJobs();
                var terminal = window.GetVisualDescendants().OfType<TerminalControl>().Single();
                await Assert.That(terminal.Focus()).IsTrue();
                await Assert.That(yield.CanExecute(null)).IsFalse();
                await Assert.That(Can()).IsTrue();
                await Assert.That(refreshItem.IsEnabled).IsTrue();
                var passedThrough = new KeyEventArgs { Key = Key.R, KeyModifiers = KeyModifiers.Control };
                new KeyBinding { Gesture = new KeyGesture(Key.R, KeyModifiers.Control), Command = yield }.TryHandle(passedThrough);
                await Assert.That(passedThrough.Handled).IsFalse();
                await Assert.That(work.IsRefreshing).IsFalse();
                workspace.ShowChatCommand.Execute().Subscribe();
                Dispatcher.UIThread.RunJobs();
                await Assert.That(yield.CanExecute(null)).IsTrue();

                var gate = source.Gate();
                var beforePoll = source.Requested.Count;
                time.Advance(WorkContextViewModel.PollInterval);
                await Assert.That(work.IsReading).IsTrue();
                await Assert.That(work.IsRefreshing).IsFalse();
                await Assert.That(Can()).IsTrue();
                await Assert.That(source.Requested.Count).IsEqualTo(beforePoll + 1);

                bindings[0].Command!.Execute(null);
                await Assert.That(work.IsRefreshing).IsTrue();
                await Assert.That(source.Requested.Count).IsEqualTo(beforePoll + 1);
                await Assert.That(Can()).IsFalse();
                await Assert.That(refreshItem.IsEnabled).IsFalse();

                var parked = work.PendingReadForTesting!;
                gate.SetResult(WorkContextRead.Of(WorkContextReadKind.SessionUnknown));
                await parked;
                if (work.PendingReadForTesting is { } follow && !ReferenceEquals(follow, parked)) await follow;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(work.IsRefreshing).IsFalse();
                await Assert.That(Can()).IsTrue();
                await Assert.That(refreshItem.IsEnabled).IsTrue();

                vm.CloseWorkspace();
                await Assert.That(Can()).IsFalse();
                await Assert.That(refreshItem.IsEnabled).IsFalse();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    /// The tabless boot: the window opens on the Sessions surface — rail plus
    /// the launcher pane — with no TabControl anywhere in its visual tree; the rail's New session
    /// row is the deselect-to-launcher affordance.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_boots_tabless_on_the_sessions_surface() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var ok = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                service.SnapshotsSubject.OnNext(Snap());
                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var noTabs = !window.GetVisualDescendants().OfType<TabControl>().Any();
                var railPresent = window.GetVisualDescendants().OfType<SessionRailView>().Any();
                var newSessionRow = window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "RailNewSessionButton");
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return noTabs && railPresent && newSessionRow;
            });
            await Assert.That(ok).IsTrue();
        });
    }

    /// Help and support is reachable from the rail footer whatever the server says: the button and
    /// Documentation stay enabled with no feedback action, and only the two report items follow it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rail_footer_offers_help_with_docs_always_and_reports_only_with_a_server() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, openFeedback: null);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var help = window.FindDescendantOfType<SessionRailView>()!.FindControl<Button>("RailHelpButton")!;
            await Assert.That(help.IsEnabled).IsTrue();
            await Assert.That(DockPanel.GetDock(help)).IsEqualTo(Dock.Right);
            await Assert.That(ToolTip.GetTip(help)).IsEqualTo("Help and support");
            await Assert.That(AutomationProperties.GetName(help)).IsEqualTo("Help and support");

            // Opened, because Flyout content only joins a tree — and so only binds — once its
            // presenter exists; unopened the report rows would all read enabled from unset CanExecute.
            var flyout = (Flyout)help.Flyout!;
            flyout.ShowAt(help);
            Dispatcher.UIThread.RunJobs();
            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var docs = rail.FindControl<Button>("RailHelpDocsButton")!;
            var bug = rail.FindControl<Button>("RailHelpBugButton")!;
            var feedback = rail.FindControl<Button>("RailHelpFeedbackButton")!;
            await Assert.That(docs.Content).IsEqualTo("Documentation");
            await Assert.That(bug.Content).IsEqualTo("Report a bug…");
            await Assert.That(feedback.Content).IsEqualTo("Send feedback…");
            await Assert.That(docs.IsEffectivelyEnabled).IsTrue();
            await Assert.That(bug.IsEffectivelyEnabled).IsFalse();
            await Assert.That(feedback.IsEffectivelyEnabled).IsFalse();

            var presenter = docs.FindAncestorOfType<FlyoutPresenter>()!;
            await Assert.That(presenter.Classes.Contains("kcapPanel")).IsTrue();
            await Assert.That(presenter.CornerRadius).IsEqualTo(new CornerRadius(12));
            await Assert.That(docs.Classes.Contains("kcapGhost")).IsTrue();

            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// Off macOS the help flyout carries the app menu's Settings, Changelog and version — the only
    /// in-window route to Settings where no native menu bar is drawn. Settings stays inert until the
    /// app hands it an action.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public Task Rail_help_carries_the_app_menu_when_no_native_menu_is_drawn(bool inWindow) => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        var settings = new System.Reactive.Subjects.BehaviorSubject<Action?>(null);
        var opened = 0;
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            settingsAction: settings, appMenuInWindow: inWindow);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var help = rail.FindControl<Button>("RailHelpButton")!;
            var flyout = (Flyout)help.Flyout!;
            flyout.ShowAt(help);
            Dispatcher.UIThread.RunJobs();

            var items = rail.FindControl<StackPanel>("RailAppMenuItems")!;
            var settingsButton = rail.FindControl<Button>("RailHelpSettingsButton")!;
            await Assert.That(items.IsVisible).IsEqualTo(inWindow);
            await Assert.That(settingsButton.Content).IsEqualTo("Settings…");
            await Assert.That(rail.FindControl<Button>("RailHelpChangelogButton")!.Content).IsEqualTo("Changelog");
            await Assert.That(rail.FindControl<TextBlock>("RailAppVersionText")!.Text).StartsWith("Kurrent Capacitor ");
            await Assert.That(settingsButton.IsEffectivelyEnabled).IsFalse();

            settings.OnNext(() => opened++);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(settingsButton.IsEffectivelyEnabled).IsTrue();
            settingsButton.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(opened).IsEqualTo(1);

            flyout.Hide();
            Dispatcher.UIThread.RunJobs();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// Hover copy names what each footer fragment is. Extra info the compact row dropped (URL,
    /// lane diagnostic, pending-update copy) sits on the lighter line; the fragment's name is
    /// the darker caption under it, or the whole tip when there is nothing extra. Visible
    /// strings (org slug, daemon name, semver) do not repeat. Same dispatcher scheduler as the
    /// compact-footer render test: an immediate OAPH notifies before its value is readable and
    /// a binding keeps the stale one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_footer_tooltips_identify_org_daemon_version_and_server() {
        var tips = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            service.SnapshotsSubject.OnNext(Snap(
                daemon: "daemon-a", version: "1.2.3+abc", serverUrl: "http://localhost:9999",
                connection: "connected", active: 1, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent", laneStatus: lane.Status);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var connection = rail.FindControl<StackPanel>("RailConnectionStatus")!;
            var tenant = rail.FindControl<TextBlock>("RailTenantText")!;
            var daemon = rail.FindControl<TextBlock>("RailDaemonNameText")!;
            var version = rail.FindControl<TextBlock>("RailVersionText")!;
            var agents = rail.FindControl<TextBlock>("RailAgentCountText")!;
            var connectionLines = VisibleTipLines(connection);
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Diagnostic: "diagnostic-marker"));
            Dispatcher.UIThread.RunJobs();
            var connectionDetailLines = VisibleTipLines(connection);
            var result = (
                ConnectionLines: connectionLines,
                ConnectionDetailLines: connectionDetailLines,
                TenantLines: VisibleTipLines(tenant),
                TenantText: tenant.Text,
                DaemonLines: VisibleTipLines(daemon),
                DaemonText: daemon.Text,
                VersionText: version.Text,
                VersionLines: VisibleTipLines(version),
                AgentLines: VisibleTipLines(agents));

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(tips.ConnectionLines).IsEquivalentTo(
            new[] { MainWindowViewModel.AttachStatusTip }, CollectionOrdering.Matching);
        await Assert.That(tips.ConnectionDetailLines).IsEquivalentTo(
            new[] { "diagnostic-marker", MainWindowViewModel.AttachStatusTip }, CollectionOrdering.Matching);
        await Assert.That(tips.TenantLines).IsEquivalentTo(
            new[] { "http://localhost:9999", MainWindowViewModel.ServerUrlTip }, CollectionOrdering.Matching);
        await Assert.That(tips.TenantText).IsEqualTo("kurrent");
        await Assert.That(tips.DaemonLines).IsEquivalentTo(
            new[] { MainWindowViewModel.DaemonNameTip }, CollectionOrdering.Matching);
        await Assert.That(tips.DaemonText).IsEqualTo("daemon-a");
        await Assert.That(tips.VersionText).IsEqualTo("1.2.3");
        await Assert.That(tips.VersionLines).IsEquivalentTo(
            new[] { MainWindowViewModel.VersionIdentityTip }, CollectionOrdering.Matching);
        await Assert.That(string.Join('\n', tips.VersionLines)).DoesNotContain("1.2.3+abc");
        await Assert.That(tips.AgentLines).IsEquivalentTo(
            new[] { MainWindowViewModel.AgentCountTip }, CollectionOrdering.Matching);
    }

    /// A dot belongs BETWEEN two fragments, never dangling: the count drops out the moment the
    /// attach does (the service keeps its snapshot, so name and version stay), and its separator
    /// has to leave with it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_daemon_row_separators_leave_with_the_fragment_they_precede() {
        var (connected, detached) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap(
                daemon: "daemon-a", version: "1.2.3", serverUrl: "http://localhost:9999",
                connection: "connected", active: 1, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent");
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var row = window.FindDescendantOfType<SessionRailView>()!.FindControl<Panel>("RailDaemonRow")!;
            string[] Fragments() => row.Children.OfType<TextBlock>()
                .Where(t => t.IsVisible).Select(t => t.Text ?? "").ToArray();

            var whileConnected = Fragments();
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            Dispatcher.UIThread.RunJobs();
            var whileDetached = Fragments();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (whileConnected, whileDetached);
        });

        await Assert.That(connected).IsEquivalentTo(
            new[] { "daemon-a", "\u00b7", "1.2.3", "\u00b7", "1 of 5 agents" }, CollectionOrdering.Matching);
        await Assert.That(detached).IsEquivalentTo(
            new[] { "daemon-a", "\u00b7", "1.2.3" }, CollectionOrdering.Matching);
    }

    /// A queued daemon update is the version's own status: warning paint on the semver, the
    /// pending copy on its hover, and no extra fragment that would collide with the help chip.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_version_warns_when_an_update_is_pending() {
        var shown = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var restartPending = new BehaviorSubject<bool>(true);
            service.SnapshotsSubject.OnNext(Snap(
                daemon: "nortonandreev", version: "1.1.0", serverUrl: "http://localhost:9999",
                connection: "connected", active: 2, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent", restartPending: restartPending);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var help = rail.FindControl<Button>("RailHelpButton")!;
            var version = rail.FindControl<TextBlock>("RailVersionText")!;
            var tipLines = VisibleTipLines(version);
            var helpOrigin = help.TranslatePoint(new Point(0, 0), rail)!.Value;
            var versionOrigin = version.TranslatePoint(new Point(0, 0), rail)!.Value;
            var result = (
                PendingLabel: rail.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "update pending" && t.IsVisible),
                VersionText: version.Text,
                Warning: ReferenceEquals(version.Foreground, window.FindResource("KcapWarningBrush")),
                TipLines: tipLines,
                OverlapsHelp: new Rect(helpOrigin, help.Bounds.Size)
                    .Intersects(new Rect(versionOrigin, version.Bounds.Size)));

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(shown.PendingLabel).IsFalse();
        await Assert.That(shown.VersionText).IsEqualTo("1.1.0");
        await Assert.That(shown.Warning).IsTrue();
        await Assert.That(shown.TipLines).IsEquivalentTo(
            new[] { MainWindowViewModel.RestartPendingMessage, MainWindowViewModel.VersionIdentityTip },
            CollectionOrdering.Matching);
        await Assert.That(shown.OverlapsHelp).IsFalse();
    }

    /// Signed-out is a rail diagnosis; the launcher's Sign in is on the other pane and hidden
    /// once a workspace is open, so the help flyout has to offer the same action (not the footer).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_help_flyout_offers_sign_in_when_signed_out() {
        var (visibleWhileOut, clicks, visibleWhileIn) = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var lane = new FakeServerLane();
            var clicks = 0;
            service.SnapshotsSubject.OnNext(Snap(
                daemon: "nortonandreev", version: "1.1.0", connection: "connected", active: 2, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent", laneStatus: lane.Status, requestSignIn: () => clicks++);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var help = rail.FindControl<Button>("RailHelpButton")!;
            var flyout = (Flyout)help.Flyout!;
            flyout.ShowAt(help);
            Dispatcher.UIThread.RunJobs();
            var signIn = rail.FindControl<Button>("RailHelpSignInButton")!;
            var whileOut = signIn.IsVisible && signIn.IsEffectivelyEnabled && signIn.Classes.Contains("kcapGhost");
            vm.SignInCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var whileIn = signIn.IsVisible;

            flyout.Hide();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (whileOut, clicks, whileIn);
        });

        await Assert.That(visibleWhileOut).IsTrue();
        await Assert.That(clicks).IsEqualTo(1);
        await Assert.That(visibleWhileIn).IsFalse();
    }

    /// 330 of rail plus 400 of pane must never squeeze the center column to nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task MainWindow_keeps_a_layout_floor_and_a_wider_default() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { DataContext = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System) };

            await Assert.That(window.MinWidth).IsEqualTo(WindowSizeMemory.MinWidth);
            await Assert.That(window.Width).IsEqualTo(WindowSizeMemory.DefaultWidth);
            await Assert.That(window.MinHeight).IsEqualTo(WindowSizeMemory.MinHeight);
            await Assert.That(window.Height).IsEqualTo(WindowSizeMemory.DefaultHeight);
        });
    }

    /// The priority block carries three things: the indicator line, the Reload button, and a failure line
    /// that outlives a lowered indicator until a success clears it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_priority_block_binds_indicator_button_and_failure() {
        var shown = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            var background = new BehaviorSubject<bool>(false);
            var reload = new BehaviorSubject<ReloadState?>(new ReloadState(ReloadOutcomeKind.Failed, "unit_missing", 1, "daemon-a", 1));
            var reloading = new BehaviorSubject<bool>(false);
            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", version: "1.1.0", serverUrl: "http://localhost:9999", connection: "connected", active: 2, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent", backgroundPriority: background, reloadState: reload, isReloading: reloading,
                reloadDaemon: _ => Task.CompletedTask);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var rail    = window.FindDescendantOfType<SessionRailView>()!;
            var block   = rail.FindControl<Border>("RailPriorityBlock")!;
            var line    = rail.FindControl<TextBlock>("RailPriorityText")!;
            var button  = rail.FindControl<Button>("RailReloadButton")!;
            var failure = rail.FindControl<TextBlock>("RailReloadFailureText")!;

            var withFailureOnly = (Block: block.IsVisible, Line: line.IsVisible, Button: button.IsEnabled, Failure: failure.IsVisible, FailureText: failure.Text,
                Warning: ReferenceEquals(failure.Foreground, window.FindResource("KcapWarningBrush")));

            background.OnNext(true);
            reloading.OnNext(true);
            Dispatcher.UIThread.RunJobs();
            var whileReloading = (Line: line.IsVisible, Button: button.IsEnabled, LineText: line.Text);

            reloading.OnNext(false);
            reload.OnNext(null);
            background.OnNext(false);
            Dispatcher.UIThread.RunJobs();
            var cleared = block.IsVisible;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (withFailureOnly, whileReloading, cleared);
        });

        await Assert.That(shown.withFailureOnly.Block).IsTrue();
        await Assert.That(shown.withFailureOnly.Line).IsFalse();
        await Assert.That(shown.withFailureOnly.Button).IsTrue();
        await Assert.That(shown.withFailureOnly.Failure).IsTrue();
        await Assert.That(shown.withFailureOnly.FailureText).Contains("No service unit is installed for daemon-a");
        await Assert.That(shown.withFailureOnly.Warning).IsTrue();
        await Assert.That(shown.whileReloading.Line).IsTrue();
        await Assert.That(shown.whileReloading.LineText).IsEqualTo(MainWindowViewModel.BackgroundPriorityMessage);
        await Assert.That(shown.whileReloading.Button).IsFalse();
        await Assert.That(shown.cleared).IsFalse();
    }
}

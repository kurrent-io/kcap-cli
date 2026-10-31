using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Headless;
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
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.WorkItems;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering of MainWindow against a fake Connected snapshot.
public class MainWindowSmokeTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Update_footer_keeps_identity_without_connection_churn_and_restores_live_status() => AvaloniaSession.DispatchAsync(async () => {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", connection: "connected", active: 1, max: 5));
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        using var home = new HomeViewModel(service, new AppStateStore(path), new NeverLaunchClient(),
            () => Task.FromResult(Array.Empty<string>()), TimeProvider.System);
        using var progress = new BehaviorSubject<(bool, string?)>((false, null));
        home.AttachUpdateProgress(progress);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            tenantName: "kurrent", home: home);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var status = rail.FindControl<TextBlock>("RailTenantText")!;
            await Assert.That(status.IsEffectivelyVisible).IsTrue();

            progress.OnNext((true, "Restarting the daemon"));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, ExpectedDaemonRestart.Reason, null));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(status.IsEffectivelyVisible).IsFalse();
            var footerText = rail.FindControl<StackPanel>("RailFooterPanel")!.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
            await Assert.That(footerText).Contains("kurrent");
            await Assert.That(footerText).Contains("daemon-a");
            var help = rail.FindControl<Button>("RailHelpButton")!;
            await Assert.That(help.IsEffectivelyVisible && help.IsEffectivelyEnabled).IsTrue();

            var longName = "engineering-workstation-" + new string('a', 100);
            service.SnapshotsSubject.OnNext(Snap(daemon: longName, connection: "connected", active: 1, max: 5));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var context = rail.FindControl<StackPanel>("RailUpdateContext")!;
            var nameText = context.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == longName);
            await Assert.That(nameText.Bounds.Width).IsLessThan(context.Bounds.Width);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            progress.OnNext((false, null));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(status.IsEffectivelyVisible).IsTrue();
            await Assert.That(vm.Alert).IsNull();
        } finally { window.Close(); }
        return true;
    });

    sealed class NeverLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
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
    static WorkspaceViewModel NewWorkspace(FakeDaemonClientService service, AgentActionService actions, string agentId, IPullRequestSource? pullRequests = null) =>
        new(agentId, service, actions, new FakeTerminalAttachClientFactory().Factory,
            () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(),
            new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: pullRequests);

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

    /// A healthy stack renders no status, daemon name, agent count or version in the window: only
    /// the org stays, and Settings › About holds the rest.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task MainWindow_renders_no_health_line_while_everything_works() {
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
                .Where(t => t.IsEffectivelyVisible)
                .Select(t => t.Text is { Length: > 0 } text
                    ? text
                    : string.Concat((t.Inlines ?? []).OfType<Run>().Select(r => r.Text ?? ""))));

            window.Close();
            Dispatcher.UIThread.RunJobs(); // flush the deferred Unloaded post so the VM's WhenActivated-scoped subscriptions actually get disposed before the next test runs

            return texts;
        });

        await Assert.That(rendered).Contains("kurrent");
        await Assert.That(rendered).DoesNotContain("Connected");
        await Assert.That(rendered).DoesNotContain("of 5 agents");
        await Assert.That(rendered).DoesNotContain("daemon-a");
        await Assert.That(rendered).DoesNotContain("1.2.3");
        await Assert.That(rendered).DoesNotContain("http://localhost:9999");
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
                var textAfter = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "BannerDetailText").Text;

                window.Close();
                Dispatcher.UIThread.RunJobs();
                home.Dispose();

                return (bannerInit, textUnreachable, textAfter);
            });

        await Assert.That(bannerInitially).IsTrue(); // Connecting… still has a notice
        await Assert.That(bannerTextUnreachable).IsEqualTo(NoticeText.Headline(HomeViewModel.DaemonDownNotice).Title);
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

        await Assert.That(rendered).Contains("kcap");
        await Assert.That(rendered).Contains("Couldn't stop agent-a");
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

    /// Reopening after a close shows the view the window built first, bound to the new session:
    /// a fresh view per open is what a macOS accessibility peer keeps alive.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Reopening_a_session_after_a_close_reuses_the_workspace_view() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var reuse = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(
                    service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                    workspaceFactory: agentId => NewWorkspace(service, actions, agentId));
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm.OpenSession("0123456789abcdef0123456789abcdef");
                Dispatcher.UIThread.RunJobs();
                var first = window.GetVisualDescendants().OfType<WorkspaceView>().Single();

                vm.CloseWorkspace();
                Dispatcher.UIThread.RunJobs();
                var heldWhileClosed = first.DataContext;

                vm.OpenSession("fedcba9876543210fedcba9876543210");
                Dispatcher.UIThread.RunJobs();
                var second = window.GetVisualDescendants().OfType<WorkspaceView>().Single();
                var boundTo = (second.DataContext as WorkspaceViewModel)?.AgentId;

                vm.CloseWorkspace();
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return (Same: ReferenceEquals(first, second), boundTo, heldWhileClosed);
            });

            await Assert.That(reuse.Same).IsTrue();
            await Assert.That(reuse.boundTo).IsEqualTo("fedcba9876543210fedcba9876543210");
            await Assert.That(reuse.heldWhileClosed).IsNull(); // the retained view lets the closed session go
        });
    }

    /// A local session in between hands the slot to the other template; the next remote session
    /// still gets the remote view the window built first.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_session_after_a_local_one_reuses_the_remote_view() {
        using var host = new RemoteHost();
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var reuse = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var (actions, _) = NewActions(service);
                var vm = new MainWindowViewModel(
                    service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                    workspaceFactory: agentId => NewWorkspace(service, actions, agentId),
                    remoteWorkspaceFactory: host.New);
                var window = new MainWindow { DataContext = vm };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                vm.OpenSession("remote-1", AgentOrigin.Remote);
                Dispatcher.UIThread.RunJobs();
                var first = window.GetVisualDescendants().OfType<RemoteSessionView>().Single();

                vm.OpenSession("local-1", AgentOrigin.Local);
                Dispatcher.UIThread.RunJobs();
                var localShown = window.GetVisualDescendants().OfType<WorkspaceView>().Count();

                vm.OpenSession("remote-2", AgentOrigin.Remote);
                Dispatcher.UIThread.RunJobs();
                var second = window.GetVisualDescendants().OfType<RemoteSessionView>().Single();
                var boundTo = (second.DataContext as RemoteSessionViewModel)?.AgentId;

                vm.CloseWorkspace();
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return (Same: ReferenceEquals(first, second), localShown, boundTo);
            });

            await Assert.That(reuse.localShown).IsEqualTo(1);
            await Assert.That(reuse.Same).IsTrue();
            await Assert.That(reuse.boundTo).IsEqualTo("remote-2");
        });
    }

    /// x:DataType on a DataTemplate subclass must still narrow it, or the local template would
    /// claim remote sessions too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Each_workspace_template_matches_only_its_own_model() {
        var types = await AvaloniaSession.DispatchAsync(() => {
            var window = new MainWindow();
            var host = window.FindControl<ContentControl>("WorkspaceHost")!;
            return host.DataTemplates.OfType<RetainedViewTemplate>().Select(t => t.DataType!).ToList();
        });

        await Assert.That(types).IsEquivalentTo([typeof(WorkspaceViewModel), typeof(RemoteSessionViewModel)]);
    }

    /// A shown MainWindow whose rail holds two rows, "Fix the flaky test"
    /// and "Leave this one alone", with an optional sibling checkout.
    static (MainWindowViewModel Vm, MainWindow Window) RailWindow(
            bool awaitingInput = false, int? liveSubagents = null, string? model = null, string? siblingWorktree = null,
            string? firstStatus = null, IReadOnlySet<string>? pendingIds = null, PendingLaunchDto? pending = null,
            IObservable<IReadOnlyDictionary<string, RailPullRequestSummary>>? pullRequestSummaries = null,
            IPullRequestSource? pullRequests = null, DateTime? firstCreatedAt = null) {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        if (pending is not null) service.Pending.AddOrUpdate(pending);
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a1", "agent", "claude", "/dev/alpha/wt/feature-x", firstStatus ?? "Running",
            null, null, null, firstCreatedAt ?? DateTime.UtcNow, model, null, Title: "Fix the flaky test",
            AwaitingInput: awaitingInput ? true : null, LiveSubagents: liveSubagents, SessionId: "s1"));
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a2", "agent", "claude", "/dev/alpha/wt/feature-x", "Running",
            null, null, null, DateTime.UtcNow, null, null, Title: "Leave this one alone", SessionId: "s2"));
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
            resolveRepoRoot, null, "http://localhost:9999", TimeProvider.System);
        var rail = new SessionRailViewModel(
            directory, id => vm!.OpenSession(id), _ => { }, TimeProvider.System,
            agentsWithPending: pendingIds is null ? null : Observable.Return(pendingIds),
            pullRequests: pullRequestSummaries, openPullRequest: (id, origin, subject) => vm!.OpenSessionPullRequest(id, origin, subject));
        vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            workspaceFactory: id => NewWorkspace(service, actions, id, pullRequests), rail: rail);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        return (vm, window);
    }

    static Button RailRow(MainWindow window, string text) => window.GetVisualDescendants().OfType<Button>()
        .First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rail_pagination_tracks_the_filter_and_disappears_when_fully_loaded() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        using var directory = new FakeAgentDirectory();
        using var sessions = new FakeServerSessionsService();
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, sessions: sessions);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            sessions.PagingSubject.OnNext(new ServerSessionsPaging(20, 84, 1, true, false));
            Dispatcher.UIThread.RunJobs();
            var view = window.FindDescendantOfType<SessionRailView>()!;
            var more = view.FindControl<Button>("RailShowMoreButton")!;
            var count = view.FindControl<TextBlock>("RailLoadedText")!;
            var cap = view.FindControl<TextBlock>("RailCapNote")!;
            await Assert.That(more.IsVisible).IsTrue();
            await Assert.That(more.Content).IsEqualTo("Load more ended sessions");
            await Assert.That(count.Text).IsEqualTo("20 of 84 ended sessions loaded");

            rail.State = RailState.Live;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(more.IsVisible).IsFalse();
            await Assert.That(count.IsVisible).IsFalse();
            await Assert.That(cap.IsVisible).IsFalse();

            rail.State = RailState.Ended;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(more.IsVisible).IsTrue();
            await Assert.That(count.IsVisible).IsTrue();

            sessions.PagingSubject.OnNext(new ServerSessionsPaging(4, 4, 1, false, false));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(more.IsVisible).IsFalse();
            await Assert.That(count.IsVisible).IsFalse();
            await Assert.That(cap.IsVisible).IsFalse();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Clicking_the_team_segment_changes_the_rails_scope_and_persists_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        using var sessions = new FakeServerSessionsService();
        var persisted = new List<RailPreferences>();
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, sessions: sessions, persistPreferences: persisted.Add);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try {
            var view = window.FindDescendantOfType<SessionRailView>()!;
            var mine = view.FindControl<Button>("RailScopeMineButton")!;
            var team = view.FindControl<Button>("RailScopeTeamButton")!;
            await Assert.That(mine.Classes.Contains("active")).IsTrue();

            team.Command!.Execute(team.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(rail.Scope).IsEqualTo(RailScope.Team);
            await Assert.That(team.Classes.Contains("active")).IsTrue();
            await Assert.That(mine.Classes.Contains("active")).IsFalse();
            await Assert.That(persisted.Select(p => p.Scope)).IsEquivalentTo(new[] { RailScope.Team });
        } finally {
            window.Close();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Repository_headers_collapse_the_body_and_keep_long_labels_clear_of_counts() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        directory.Rows.AddOrUpdate(AgentRow.FromLocal(new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null, null,
            Title: "Session in a long repository"), new RepoIdentity("repo:long", "nortonandreev/nortonandreev-with-a-long-repository-name")));
        directory.Rows.AddOrUpdate(AgentRow.FromLocal(new AgentStatusDto(
            "a2", "agent", "claude", null, "Running", null, null, null, DateTime.UtcNow, null, null,
            Title: "Session without checkout"), new RepoIdentity("none", "No repository")));
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try {
            var headers = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("railRepoHeader")).ToArray();
            await Assert.That(headers).Count().IsEqualTo(2);
            foreach (var header in headers) {
                var repo = (RailGroupViewModel)header.DataContext!;
                var label = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("eyebrow"));
                var count = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("sectionMeta"));
                var row = RailRow(window, repo.Sessions[0].Primary!);
                var body = row.GetVisualAncestors().OfType<Border>().Single(b => b.Classes.Contains("sectionBody"));
                var right = label.TranslatePoint(new Point(label.Bounds.Width, 0), header)!.Value.X;
                var countLeft = count.TranslatePoint(default, header)!.Value.X;
                await Assert.That(right).IsLessThan(countLeft);
                await Assert.That(label.TextTrimming).IsEqualTo(TextTrimming.CharacterEllipsis);
                await Assert.That(label.FontSize).IsEqualTo(11.5);
                await Assert.That(count.Text).IsEqualTo("1 session");
                await Assert.That(header.Focusable).IsTrue();
                await Assert.That(body.BorderThickness.Left).IsEqualTo(1);
                await Assert.That(row.IsEffectivelyVisible).IsTrue();
                var centre = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
                window.MouseMove(centre);
                Dispatcher.UIThread.RunJobs();
                var presenter = header.GetVisualDescendants().OfType<ContentPresenter>().First();
                await Assert.That((presenter.Background as ISolidColorBrush)!.Color).IsEqualTo(((ISolidColorBrush)window.FindResource("KcapSurfaceRaisedBrush")!).Color);
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(row.IsEffectivelyVisible).IsFalse();
                await Assert.That(count.IsEffectivelyVisible).IsTrue();
                await Assert.That(AutomationProperties.GetName(header)).StartsWith("Expand");
                header.Command.Execute(null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(row.IsEffectivelyVisible).IsTrue();
                await Assert.That(AutomationProperties.GetName(header)).StartsWith("Collapse");
            }
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Collapsed_repository_surfaces_idle_and_permission_status_without_reopening() => AvaloniaSession.RunOnUiAsync(async () => {
        using var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        directory.Rows.AddOrUpdate(AgentRow.FromLocal(new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null, null,
            Title: "Waiting session", AwaitingInput: true), new RepoIdentity("path:/repo", "repo")));
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, agentsWithPending: pending);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try {
            var header = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("railRepoHeader"));
            var mark = header.GetVisualDescendants().OfType<AgentStatusMark>().Single();
            await Assert.That(mark.IsEffectivelyVisible).IsFalse();
            header.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(mark.IsEffectivelyVisible).IsTrue();
            await Assert.That(mark.FindControl<TextBlock>("StatusWord")!.Text).IsEqualTo("Idle");
            await Assert.That(ToolTip.GetTip(header) as string).Contains("Waiting session — Idle");
            await Assert.That(AutomationProperties.GetName(header)).Contains("1 session idle");
            pending.OnNext(new HashSet<string> { "a1" });
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(rail.Groups[0].IsExpanded).IsFalse();
            await Assert.That(RailRow(window, "Waiting session").IsEffectivelyVisible).IsFalse();
            await Assert.That(mark.FindControl<TextBlock>("StatusWord")!.Text).IsEqualTo("Needs you");
            await Assert.That(ToolTip.GetTip(header) as string).Contains("Pending response");
            pending.OnNext(new HashSet<string>());
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mark.FindControl<TextBlock>("StatusWord")!.Text).IsEqualTo("Idle");
            header.Command.Execute(null);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(mark.IsEffectivelyVisible).IsFalse();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Multiple_pr_menu_opens_the_chosen_reader_and_closes_on_access_loss() => AvaloniaSession.RunOnUiAsync(async () => {
        var source = new FakePullRequestSource(new FakeTimeProvider());
        var items = source.Links.Select(link => new RailPullRequestMenuItem(PullRequestWire.Subject(link), $"Change {link.Number}", "Open")).ToArray();
        using var summaries = new BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(
            new Dictionary<string, RailPullRequestSummary> { ["s1"] = new(items) });
        var (vm, window) = RailWindow(pullRequestSummaries: summaries, pullRequests: source);
        try {
            var pr = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible && b.Classes.Contains("railPr"));
            pr.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var flyout = (Flyout)FlyoutBase.GetAttachedFlyout(pr)!;
            await Assert.That(flyout.IsOpen).IsTrue();
            await Assert.That(vm.CurrentWorkspace).IsNull();
            summaries.OnNext(new Dictionary<string, RailPullRequestSummary> {
                ["s1"] = summaries.Value["s1"],
                ["s2"] = new([new(PullRequestWire.Subject(source.Links[1]), null, "Draft")]),
            });
            Dispatcher.UIThread.RunJobs();
            await Assert.That(flyout.IsOpen).IsTrue();
            var options = ((StackPanel)((ScrollViewer)flyout.Content!).Content!).Children.OfType<Button>().ToArray();
            await Assert.That(options).Count().IsEqualTo(2);
            await Assert.That(AutomationProperties.GetName(options[0])).Contains("example/repo #1");
            options[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Assert.That(flyout.IsOpen).IsFalse();
            var workspace = (WorkspaceViewModel)vm.CurrentWorkspace!;
            await WorkspaceFixtures.WaitUntilAsync(() => workspace.PullRequests!.CanReveal && !workspace.PullRequests.IsReading, what: "chosen PR");
            await Assert.That(workspace.PullRequests!.Selected!.Subject.Number).IsEqualTo(1);
            pr.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Assert.That(((Flyout)FlyoutBase.GetAttachedFlyout(pr)!).IsOpen).IsTrue();
            summaries.OnNext(new Dictionary<string, RailPullRequestSummary>());
            await Assert.That(((Flyout)FlyoutBase.GetAttachedFlyout(pr)!).IsOpen).IsFalse();
            await workspace.TeardownAsync();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task Local_pr_empty_and_failed_initial_loads_keep_navigation_available(bool failed) => AvaloniaSession.RunOnUiAsync(async () => {
        var source = new FakePullRequestSource(new FakeTimeProvider()) { Links = [],
            Capability = failed ? PullRequestCapabilityKind.Unavailable : PullRequestCapabilityKind.Supported };
        var (vm, window) = RailWindow(pullRequests: source);
        try {
            vm.OpenSessionPullRequest("a1", AgentOrigin.Local);
            var workspace = (WorkspaceViewModel)vm.CurrentWorkspace!;
            await WorkspaceFixtures.WaitUntilAsync(() => failed ? workspace.PullRequests!.Notice is { } note && note != "Loading pull requests…" : workspace.PullRequests!.HasListed,
                what: "first list settled");
            await Assert.That(workspace.IsPullRequestActive).IsEqualTo(failed);
            await Assert.That(workspace.ShowsPullRequestTab).IsEqualTo(failed);
            await workspace.TeardownAsync();
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pr_link_is_a_separate_focusable_control_and_opens_the_native_reader() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var time = new FakeTimeProvider();
            var source = new FakePullRequestSource(time) { Links = [FakePullRequestSource.Link(142), FakePullRequestSource.Link(143)] };
            using var summaries = new BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([new(PullRequestWire.Subject(source.Links[0]), null, "Open")]) });
            var (vm, window) = RailWindow(pullRequestSummaries: summaries, pullRequests: source,
                firstCreatedAt: DateTime.UtcNow.AddMinutes(-90));
            try {
                var session = RailRow(window, "Fix the flaky test");
                var pr = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible && b.Classes.Contains("railPr"));
                await Assert.That(pr.Focusable).IsTrue();
                await Assert.That(pr.GetVisualAncestors().Contains(session)).IsFalse();
                var card = session.FindAncestorOfType<Border>()!;
                await Assert.That(card.Classes.Contains("railSession")).IsTrue();
                await Assert.That(pr.GetVisualAncestors().Contains(card)).IsTrue();
                await Assert.That(pr.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "PR #142")).IsTrue();
                var mark = pr.GetVisualDescendants().OfType<PullRequestStatusLabel>().Single();
                await Assert.That(mark.ShowText).IsFalse();
                await Assert.That(pr.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Open" && t.IsEffectivelyVisible)).IsFalse();
                await Assert.That((mark.DataContext as PullRequestStatus)!.Kind).IsEqualTo("open");
                await Assert.That(ToolTip.GetTip(pr) as string).Contains("Open");
                await Assert.That((pr.Background as ISolidColorBrush)!.Color.A).IsEqualTo((byte)0);
                await Assert.That(pr.Bounds.Width).IsLessThan(card.Bounds.Width / 2);
                var age = session.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionMeta");
                var prLabel = pr.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "PR #142");
                double Baseline(TextBlock text) => text.TranslatePoint(new Point(0, text.TextLayout.Baseline), card)!.Value.Y;
                await Assert.That(Math.Abs(Baseline(age) - Baseline(prLabel))).IsLessThan(2);
                await Assert.That(age.Text).IsEqualTo("1h 30m");
                await Assert.That(age.Bounds.Width).IsGreaterThanOrEqualTo(NaturalWidth(age) - 1);
                var ageRight = age.TranslatePoint(new Point(age.Bounds.Width, 0), card)!.Value.X;
                var prLeft = pr.TranslatePoint(default, card)!.Value.X;
                await Assert.That(ageRight).IsLessThan(prLeft);
                await Assert.That(ToolTip.GetTip(pr) as string).Contains("Linked to this session: Fix the flaky test");
                await Assert.That(AutomationProperties.GetName(pr)).Contains("Fix the flaky test");
                pr.Command!.Execute(null);
                var workspace = (WorkspaceViewModel)vm.CurrentWorkspace!;
                await WorkspaceFixtures.WaitUntilAsync(() => workspace.PullRequests!.CanReveal && !workspace.PullRequests.IsReading,
                    what: "native PR reader");
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(workspace.AgentId).IsEqualTo("a1");
                await Assert.That(workspace.IsPullRequestActive).IsTrue();
                await Assert.That(card.Classes.Contains("selected")).IsTrue();
                await Assert.That((card.Background as ISolidColorBrush)!.Color.A).IsGreaterThan((byte)0);
                await Assert.That(window.GetVisualDescendants().OfType<PullRequestReader>().Any()).IsTrue();
                await Assert.That(workspace.PullRequests!.Selected!.Subject.Number).IsEqualTo(142);
                foreach (var lifecycle in new[] { "Draft", "Merged", "Closed", "Open" }) {
                    summaries.OnNext(new Dictionary<string, RailPullRequestSummary> {
                        ["s1"] = new([new(PullRequestWire.Subject(source.Links[0]), null, lifecycle)]),
                    });
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    await Assert.That(mark.IsEffectivelyVisible).IsTrue();
                    await Assert.That((mark.DataContext as PullRequestStatus)!.Kind).IsEqualTo(lifecycle.ToLowerInvariant());
                    await Assert.That(pr.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == lifecycle && t.IsEffectivelyVisible)).IsFalse();
                    await Assert.That(ToolTip.GetTip(pr) as string).Contains(lifecycle);
                    await Assert.That(AutomationProperties.GetName(pr)).Contains(lifecycle);
                    var stateText = mark.FindControl<TextBlock>("StatusText")!;
                    var brush = lifecycle == "Merged" ? "KcapSuccessBrush" : lifecycle == "Closed" ? "KcapDangerBrush" : "KcapMutedBrush";
                    await Assert.That((stateText.Foreground as ISolidColorBrush)!.Color).IsEqualTo(((ISolidColorBrush)window.FindResource(brush)!).Color);
                }
                summaries.OnNext(new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([
                    new(PullRequestWire.Subject(FakePullRequestSource.Link(1)), null, "Open"),
                    new(PullRequestWire.Subject(FakePullRequestSource.Link(2)), null, "Open"),
                    new(PullRequestWire.Subject(FakePullRequestSource.Link(3)), null, "Open"),
                ]) });
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var updatedPr = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsVisible && b.Classes.Contains("railPr"));
                await Assert.That(updatedPr.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "3 PRs")).IsTrue();
                await Assert.That(updatedPr.GetVisualDescendants().OfType<PullRequestStatusLabel>().Single().IsVisible).IsFalse();
                summaries.OnNext(new Dictionary<string, RailPullRequestSummary>());
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(updatedPr.IsVisible).IsFalse();
                await workspace.TeardownAsync();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Remote_machine_badge_and_tooltip_identify_the_daemon_in_both_selection_states() => AvaloniaSession.RunOnUiAsync(async () => {
        const string machine = "rail-qa-with-a-long-daemon-name";
        var service = new FakeDaemonClientService();
        service.StatusSubject.OnNext(new(AttachState.Connected, null, null));
        using var directory = new FakeAgentDirectory();
        directory.Rows.AddOrUpdate(AgentRow.FromRemote(new Capacitor.Remote.Models.AgentInstanceDto {
            AgentId = "r1", Status = "idle", DaemonName = machine, OwnerUserId = "u1", Vendor = "claude",
            Prompt = "File listing", RepoOwner = "o", RepoName = "repo", RepoPath = "/remote/repo", RegisteredAt = DateTime.UtcNow,
        }));
        using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System, rail: rail);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try {
            var row = RailRow(window, "File listing");
            var badge = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SessionMachineBadge");
            var label = badge.GetVisualDescendants().OfType<TextBlock>().Single();
            await Assert.That(label.Text).IsEqualTo($"on {machine}");
            await Assert.That(((RailSessionViewModel)row.DataContext!).Meta).IsEqualTo("");
            await Assert.That(row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionMeta").IsEffectivelyVisible).IsFalse();
            await Assert.That(badge.IsEffectivelyVisible).IsTrue();
            await Assert.That(badge.Bounds.Width).IsGreaterThan(0);
            await Assert.That(badge.BorderThickness).IsEqualTo(new Thickness(0));
            await Assert.That((label.Foreground as ISolidColorBrush)!.Color).IsEqualTo(((ISolidColorBrush)window.FindResource("KcapMutedBrush")!).Color);
            await Assert.That(ToolTip.GetTip(badge) as string).Contains(machine);
            await Assert.That(ToolTip.GetTip(badge) as string).Contains("remote daemon");
            var restingColor = (label.Foreground as ISolidColorBrush)!.Color;
            ToolTip.SetIsOpen(row, true);
            Dispatcher.UIThread.RunJobs();
            var tip = (StackPanel)ToolTip.GetTip(row)!;
            var machineLines = tip.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBlock>()).Select(t => t.Text).ToArray();
            await Assert.That(machineLines).Contains(machine);
            await Assert.That(machineLines).Contains("Machine · remote daemon");
            ToolTip.SetIsOpen(row, false);
            rail.SelectedSessionKey = "remote:r1";
            Dispatcher.UIThread.RunJobs();
            await Assert.That(badge.IsEffectivelyVisible).IsTrue();
            await Assert.That((label.Foreground as ISolidColorBrush)!.Color).IsEqualTo(restingColor);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// The rail's own click path: a session row rendered by SessionRailView carries the
    /// VM's OpenCommand, and executing it opens that agent's workspace.
    ///
    /// Also pins the selection highlight as RENDERED state, not just as a bound class. A row's
    /// resting Background must come from the `railRow` class style: a local `Background` attribute
    /// on the Button would be a LocalValue, outrank the `.selected` style
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
                    SiblingAlpha: Alpha(Row("Leave this one alone")));
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(opened.AgentId).IsEqualTo("a1");
            await Assert.That(opened.SelectedClass).IsTrue();
            await Assert.That(opened.SelectedAlpha).IsGreaterThan((byte)0); // the highlight actually paints
            await Assert.That(opened.SiblingAlpha).IsEqualTo((byte)0); // an unopened row stays transparent
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sessions_from_different_checkouts_share_the_same_indent_without_headers() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var seen = await AvaloniaSession.DispatchAsync(() => {
                var (_, window) = RailWindow(siblingWorktree: "x");
                window.UpdateLayout();
                var first = RailRow(window, "Fix the flaky test");
                var other = RailRow(window, "Sibling work");
                ToolTip.SetIsOpen(first, true);
                Dispatcher.UIThread.RunJobs();
                var result = (
                    Offset: Math.Abs(first.TranslatePoint(default, window)!.Value.X - other.TranslatePoint(default, window)!.Value.X),
                    HasHeader: window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text is "feature-x" or "x"),
                    Tip: ToolTip.GetTip(first) is StackPanel panel
                        ? panel.Children.OfType<StackPanel>().SelectMany(p => p.Children.OfType<TextBlock>()).First(t => t.Text == "/dev/alpha/wt/feature-x").Text
                        : null);
                window.Close();
                Dispatcher.UIThread.RunJobs();
                return result;
            });
            await Assert.That(seen.Offset).IsLessThan(0.5);
            await Assert.That(seen.HasHeader).IsFalse();
            await Assert.That(seen.Tip).Contains("/dev/alpha/wt/feature-x");
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
                var meta = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionMeta");
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
                    MarkRight: Right(mark),
                    MetaLeft: Left(meta),
                    ModelInRow: row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "opus"),
                    ChildTips: row.GetVisualDescendants().Count(v => v is Control c && c.IsEffectivelyVisible && ToolTip.GetTip(c) is not null),
                    RowTipIsStatus: ToolTip.GetTip(row) is StackPanel tip && tip.Children.OfType<AgentStatusTip>().Any(),
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
            await Assert.That(seen.MarkRight).IsLessThan(seen.MetaLeft);
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
                var pendingMeta = pendingRow.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "SessionMeta");
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

    /// Windows draws no native app menu to carry ⌘,, so the window itself binds Ctrl+, to Settings.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Settings_shortcut_is_bound_to_the_settings_command_on_Windows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (bound, isSettings) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
                var window = new MainWindow { DataContext = vm };
                var binding = window.KeyBindings.SingleOrDefault(
                    k => k.Gesture is { Key: Key.OemComma, KeyModifiers: KeyModifiers.Control });
                var result = (binding is not null, ReferenceEquals(binding?.Command, vm.OpenSettingsCommand));
                window.Close();
                return result;
            });

            await Assert.That(bound).IsEqualTo(OperatingSystem.IsWindows());
            if (bound) await Assert.That(isSettings).IsTrue();
        });
    }

    /// F11 on Windows: full screen, then back to the state it left, maximized included.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Full_screen_shortcut_toggles_and_restores_the_previous_state_on_Windows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (bound, entered, restored) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System);
                var window = new MainWindow { DataContext = vm };
                window.Show();
                window.WindowState = WindowState.Maximized;
                var binding = window.KeyBindings.SingleOrDefault(k => k.Gesture is { Key: Key.F11, KeyModifiers: KeyModifiers.None });
                binding?.Command.Execute(null);
                var inFullScreen = window.WindowState;
                binding?.Command.Execute(null);
                var after = window.WindowState;
                window.Close();
                return (binding is not null, inFullScreen, after);
            });

            await Assert.That(bound).IsEqualTo(OperatingSystem.IsWindows());
            if (!bound) return;
            await Assert.That(entered).IsEqualTo(WindowState.FullScreen);
            await Assert.That(restored).IsEqualTo(WindowState.Maximized);
        });
    }

    /// Cmd+N / Ctrl+N: the window binds the advertised New session shortcut, which drops an open
    /// workspace back to the launcher (the new-session empty state) and, pressed again there,
    /// focuses the goal and rings its card.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task New_session_shortcut_is_bound_and_returns_to_the_launcher() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (hadWorkspace, hasMeta, hasCtrl, afterInvoke, nudged, goalFocused) = await AvaloniaSession.DispatchAsync(() => {
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

                var newSession = window.KeyBindings
                    .First(k => k.Gesture is { Key: Key.N } g && g.KeyModifiers.HasFlag(KeyModifiers.Meta)).Command!;
                newSession.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var after = vm.CurrentWorkspace;
                var nudgedOnClose = window.LauncherView.IsNudgingForTesting;

                window.Focus();
                newSession.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var ringed = !nudgedOnClose && window.LauncherView.IsNudgingForTesting;
                var focused = window.LauncherView.GoalInput.IsFocused;

                window.Close();
                Dispatcher.UIThread.RunJobs();
                return (had, meta, ctrl, after, ringed, focused);
            });
            await Assert.That(hadWorkspace).IsTrue();
            await Assert.That(hasMeta).IsTrue();
            await Assert.That(hasCtrl).IsTrue();
            await Assert.That(afterInvoke).IsNull();
            await Assert.That(nudged).IsTrue();
            await Assert.That(goalFocused).IsTrue();
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
                var yield = new UnlessTerminalFocused(window, m => m.RefreshWorkCommand);

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

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    [NotInParallel("AvaloniaSession")]
    public Task Help_opens_the_dashboard_from_an_open_workspace_on_every_platform(bool appMenuInWindow) => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        var (actions, _) = NewActions(service);
        var opener = new RecordingOpener();
        using var web = new WebWorkspaceViewModel("https://team.example", opener, _ => { }, CancellationToken.None);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            appMenuInWindow: appMenuInWindow,
            workspaceFactory: id => new WorkspaceViewModel(id, service, actions, new FakeTerminalAttachClientFactory().Factory,
                () => new FakeTerminalSurface(), new FakeTimeProvider(), opener, new FakePermissionService(),
                new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader())) { Web = web };
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            vm.OpenSession("0123456789abcdef0123456789abcdef");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.CurrentWorkspace).IsNotNull();
            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var help = rail.FindControl<Button>("RailHelpButton")!;
            var flyout = (Flyout)help.Flyout!;
            flyout.ShowAt(help);
            Dispatcher.UIThread.RunJobs();
            var dashboard = rail.FindControl<Button>("RailOpenDashboardButton")!;
            await Assert.That(dashboard.IsEffectivelyVisible).IsTrue();
            await Assert.That(dashboard.IsEffectivelyEnabled).IsTrue();
            await Assert.That(AutomationProperties.GetName(dashboard)).IsEqualTo("Open dashboard");
            ClickFlyoutButton(dashboard);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(flyout.IsOpen).IsFalse();
            await Assert.That(opener.Opened.Single()).IsEqualTo("https://team.example");
        } finally {
            vm.CloseWorkspace();
            window.Close();
        }
    });

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
            await Assert.That(Grid.GetColumn(help)).IsEqualTo(1);
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rail_help_flyout_items_close_the_flyout_and_invoke_their_commands_when_clicked() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        var opener = new RecordingOpener();
        var reported = new List<Capacitor.Cli.Core.Commands.FeedbackCategory>();
        var setupOpened = 0;
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            openFeedback: reported.Add, opener: opener, requestSetup: () => setupOpened++);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var help = rail.FindControl<Button>("RailHelpButton")!;
            var flyout = (Flyout)help.Flyout!;

            void OpenAndClick(string buttonName) {
                flyout.ShowAt(help);
                Dispatcher.UIThread.RunJobs();
                ClickFlyoutButton(rail.FindControl<Button>(buttonName)!);
            }

            OpenAndClick("RailHelpSetupButton");
            await Assert.That(flyout.IsOpen).IsFalse();
            await Assert.That(setupOpened).IsEqualTo(1);

            OpenAndClick("RailHelpDocsButton");
            await Assert.That(flyout.IsOpen).IsFalse();
            await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.DocsUrl]);

            OpenAndClick("RailHelpBugButton");
            await Assert.That(flyout.IsOpen).IsFalse();
            await Assert.That(reported).IsEquivalentTo([Capacitor.Cli.Core.Commands.FeedbackCategory.Bug]);

            OpenAndClick("RailHelpFeedbackButton");
            await Assert.That(flyout.IsOpen).IsFalse();
            await Assert.That(reported).IsEquivalentTo([
                Capacitor.Cli.Core.Commands.FeedbackCategory.Bug,
                Capacitor.Cli.Core.Commands.FeedbackCategory.Feedback,
            ]);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// Off macOS the help flyout carries the app menu's Settings, Changelog and version — the only
    /// in-window route to Settings where no native menu bar is drawn. Settings stays inert until the
    /// app hands it an action, and Uninstall is not shown until the app can remove itself.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public Task Rail_help_carries_the_app_menu_when_no_native_menu_is_drawn(bool inWindow) => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        var settings = new System.Reactive.Subjects.BehaviorSubject<Action?>(null);
        var uninstall = new System.Reactive.Subjects.BehaviorSubject<Action?>(null);
        var opener = new RecordingOpener();
        var opened = 0;
        var uninstalls = 0;
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            opener: opener, settingsAction: settings, uninstallAction: uninstall, appMenuInWindow: inWindow);
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
            var changelogButton = rail.FindControl<Button>("RailHelpChangelogButton")!;
            await Assert.That(items.IsVisible).IsEqualTo(inWindow);
            await Assert.That(settingsButton.Content).IsEqualTo("Settings…");
            await Assert.That(changelogButton.Content).IsEqualTo("Changelog");
            await Assert.That(rail.FindControl<TextBlock>("RailAppVersionText")!.Text).StartsWith("Desktop app ");
            await Assert.That(settingsButton.IsEffectivelyEnabled).IsFalse();
            var uninstallButton = rail.FindControl<Button>("RailHelpUninstallButton")!;
            await Assert.That(uninstallButton.IsVisible).IsFalse();

            settings.OnNext(() => opened++);
            uninstall.OnNext(() => uninstalls++);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(settingsButton.IsEffectivelyEnabled).IsTrue();
            await Assert.That(uninstallButton.IsEffectivelyVisible).IsEqualTo(inWindow);
            if (inWindow) {
                ClickFlyoutButton(settingsButton);
                await Assert.That(flyout.IsOpen).IsFalse();
                await Assert.That(opened).IsEqualTo(1);

                flyout.ShowAt(help);
                Dispatcher.UIThread.RunJobs();
                ClickFlyoutButton(changelogButton);
                await Assert.That(flyout.IsOpen).IsFalse();
                await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.ChangelogUrl]);

                flyout.ShowAt(help);
                Dispatcher.UIThread.RunJobs();
                ClickFlyoutButton(uninstallButton);
                await Assert.That(flyout.IsOpen).IsFalse();
                await Assert.That(uninstalls).IsEqualTo(1);
            } else {
                flyout.Hide();
                Dispatcher.UIThread.RunJobs();
            }
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// A help item closes the flyout from its Click handler, which runs before the button's command.
    /// The command must still run: a flyout that detached its content first would leave every item dead.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rail_help_items_run_their_command_when_clicked() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        var opener = new RecordingOpener();
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            appMenuInWindow: true, opener: opener);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var help = rail.FindControl<Button>("RailHelpButton")!;
            var onClick = typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (var name in new[] { "RailHelpDocsButton", "RailHelpChangelogButton" }) {
                help.Flyout!.ShowAt(help);
                Dispatcher.UIThread.RunJobs();
                onClick.Invoke(rail.FindControl<Button>(name)!, null);
                Dispatcher.UIThread.RunJobs();
            }

            await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.DocsUrl, AppMenuBar.ChangelogUrl]);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// Windows has no native About panel, so the rail's help flyout opens the app's own, and a
    /// second click brings that one forward rather than stacking another.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rail_help_opens_one_About_window_on_Windows() => AvaloniaSession.RunOnUiAsync(async () => {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        var opener = new RecordingOpener();
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            appMenuInWindow: true, opener: opener);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            // Opened first: the flyout's rows only bind once they join a tree.
            var help = rail.FindControl<Button>("RailHelpButton")!;
            ((Flyout)help.Flyout!).ShowAt(help);
            Dispatcher.UIThread.RunJobs();
            var about = rail.FindControl<Button>("RailHelpAboutButton")!;
            await Assert.That(about.IsVisible).IsEqualTo(OperatingSystem.IsWindows());
            if (!OperatingSystem.IsWindows()) return;

            about.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            about.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var opened = window.OwnedWindows.OfType<AboutWindow>().ToArray();
            await Assert.That(opened.Length).IsEqualTo(1);
            await Assert.That(opened[0].FindControl<TextBlock>("AboutVersionText")!.Text).StartsWith("Version ");

            var docs = opened[0].FindControl<Button>("AboutDocsButton")!;
            var changelog = opened[0].FindControl<Button>("AboutChangelogButton")!;
            await Assert.That(docs.IsEffectivelyEnabled).IsTrue();
            await Assert.That(changelog.IsEffectivelyEnabled).IsTrue();
            docs.Command!.Execute(null);
            changelog.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(opener.Opened).IsEquivalentTo([AppMenuBar.DocsUrl, AppMenuBar.ChangelogUrl]);
        } finally {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// A healthy footer carries no status. A stopped daemon shows the alert with its long copy on
    /// hover, and its Start runs the same start as the launcher's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_footer_shows_an_alert_and_its_action_only_when_something_needs_the_user() {
        var shown = await AvaloniaSession.DispatchAsync(() => {
            var service = new FakeDaemonClientService();
            service.SnapshotsSubject.OnNext(Snap(daemon: "daemon-a", connection: "connected", active: 1, max: 5));
            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "kurrent");
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var rail = window.FindDescendantOfType<SessionRailView>()!;
            var row = rail.FindControl<StackPanel>("RailAlertRow")!;
            var footer = rail.FindControl<StackPanel>("RailFooterPanel")!;
            var healthyRow = row.IsEffectivelyVisible;
            var healthyTexts = footer.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var text = rail.FindControl<TextBlock>("RailAlertText")!;
            var action = rail.FindControl<Button>("RailAlertActionButton")!;
            var result = (
                HealthyRow: healthyRow,
                HealthyTexts: healthyTexts,
                Text: row.IsEffectivelyVisible ? text.Text : null,
                Tip: ToolTip.GetTip(text) as string,
                ActionVisible: action.IsEffectivelyVisible,
                ActionLabel: action.Content as string,
                Starts: 0);
            ClickFlyoutButton(action);
            result.Starts = service.StartDaemonCallCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(shown.HealthyRow).IsFalse();
        await Assert.That(shown.HealthyTexts).Contains("kurrent");
        await Assert.That(shown.HealthyTexts).DoesNotContain("daemon-a");
        await Assert.That(shown.Text).IsEqualTo(MainWindowViewModel.DaemonStoppedText);
        await Assert.That(shown.Tip).IsEqualTo(HomeViewModel.DaemonDownNotice);
        await Assert.That(shown.ActionVisible).IsTrue();
        await Assert.That(shown.ActionLabel).IsEqualTo("Start daemon");
        await Assert.That(shown.Starts).IsEqualTo(1);
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
            ClickFlyoutButton(signIn);
            var closedOnClick = !flyout.IsOpen;

            flyout.ShowAt(help);
            Dispatcher.UIThread.RunJobs();
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var whileIn = signIn.IsVisible;

            flyout.Hide();
            window.Close();
            Dispatcher.UIThread.RunJobs();
            return (whileOut && closedOnClick, clicks, whileIn);
        });

        await Assert.That(visibleWhileOut).IsTrue();
        await Assert.That(clicks).IsEqualTo(1);
        await Assert.That(visibleWhileIn).IsFalse();
    }

    internal static void ClickFlyoutButton(Button button) {
        Dispatcher.UIThread.RunJobs();
        var top = TopLevel.GetTopLevel(button)
            ?? throw new InvalidOperationException("Flyout button is not attached to a TopLevel.");
        top.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), top)
            ?? throw new InvalidOperationException("Flyout button has no coordinates in its TopLevel.");
        top.MouseDown(point, MouseButton.Left);
        top.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
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

            var withFailureOnly = (Block: block.IsVisible, Line: line.IsVisible, Button: button.IsEffectivelyEnabled, Failure: failure.IsVisible, FailureText: failure.Text,
                Warning: ReferenceEquals(failure.Foreground, window.FindResource("KcapWarningBrush")));

            background.OnNext(true);
            reloading.OnNext(true);
            Dispatcher.UIThread.RunJobs();
            var whileReloading = (Line: line.IsVisible, Button: button.IsEffectivelyEnabled, LineText: line.Text);

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

    static Point CentreOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    static void Click(SearchWindowHost host, Control control) {
        var at = CentreOf(control, host.Window);
        host.Window.MouseMove(at);
        host.Window.MouseDown(at, MouseButton.Left);
        host.Window.MouseUp(at, MouseButton.Left);
        host.Settle();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rail_shows_search_field_and_hides_filters_until_toggled() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var field = host.Named<Button>("RailSearchButton");
        var filter = host.Named<Button>("RailFilterButton");
        var controls = host.Named<StackPanel>("RailControls");
        var placeholder = host.Named<TextBlock>("RailSearchText").Text;
        var shortcut = host.Named<TextBlock>("RailSearchShortcut");
        var collapsed = controls.IsVisible;

        Click(host, filter);
        var expanded = controls.IsVisible;
        var active = filter.Classes.Contains("active");
        Click(host, filter);

        await Assert.That(field.IsEffectivelyVisible).IsTrue();
        await Assert.That(AutomationProperties.GetName(field)).IsEqualTo("Search sessions");
        await Assert.That(placeholder).IsEqualTo("Search…");
        await Assert.That(shortcut.Text).IsEqualTo(ShortcutLabels.Search);
        await Assert.That(shortcut.IsVisible).IsTrue();
        await Assert.That(collapsed).IsFalse();
        await Assert.That(expanded).IsTrue();
        await Assert.That(active).IsTrue();
        await Assert.That(controls.IsVisible).IsFalse();
        await Assert.That(filter.Classes.Contains("active")).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Filter_dot_follows_filters_differ() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var dot = host.Named<Ellipse>("RailFilterDot");
        var atDefaults = dot.IsVisible;

        host.Rail.Scope = RailScope.Team;
        host.Settle();
        var narrowed = dot.IsVisible;
        host.Rail.Scope = RailScope.Mine;
        host.Settle();

        await Assert.That(atDefaults).IsFalse();
        await Assert.That(narrowed).IsTrue();
        await Assert.That(dot.IsVisible).IsFalse();
        await Assert.That(dot.Fill).IsSameReferenceAs(Application.Current!.FindResource("KcapInfoBrush"));
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Meta_k_opens_the_cover_and_focuses_the_query() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var cover = host.Named<SessionSearchView>("SearchCover");
        var hiddenBefore = !cover.IsVisible;

        host.Window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Meta);
        host.Settle();

        await Assert.That(hiddenBefore).IsTrue();
        await Assert.That(host.Vm.IsSearchOpen).IsTrue();
        await Assert.That(cover.IsEffectivelyVisible).IsTrue();
        await Assert.That(host.Named<Grid>("LauncherPane").IsVisible).IsFalse();
        await Assert.That(host.Focused).IsSameReferenceAs(host.Named<TextBox>("SearchQueryBox"));
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Escape_in_the_query_hides_the_cover_and_keeps_the_workspace_view() => AvaloniaSession.RunOnUiAsync(async () => {
        WorkspaceViewModel? built = null;
        FakeDaemonClientService? service = null;
        using var host = new SearchWindowHost(workspaceFactory: id => built = NewWorkspace(service!, NewActions(service!).Actions, id));
        service = host.Service;
        host.Vm.OpenSession("0123456789abcdef0123456789abcdef");
        host.Settle();
        var workspaceHost = host.Named<ContentControl>("WorkspaceHost");
        var view = workspaceHost.GetVisualDescendants().OfType<WorkspaceView>().Single();

        host.Vm.OpenSearchCommand.Execute().Subscribe();
        host.Settle();
        var coveredHidden = !workspaceHost.IsVisible;
        var stillInTree = view.IsAttachedToVisualTree();
        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        host.Settle();

        await Assert.That(coveredHidden).IsTrue();
        await Assert.That(stillInTree).IsTrue();
        await Assert.That(host.Vm.IsSearchOpen).IsFalse();
        await Assert.That(host.Named<SessionSearchView>("SearchCover").IsVisible).IsFalse();
        await Assert.That(workspaceHost.IsVisible).IsTrue();
        await Assert.That(host.Vm.CurrentWorkspace).IsSameReferenceAs(built);
        await Assert.That(workspaceHost.GetVisualDescendants().OfType<WorkspaceView>().Single()).IsSameReferenceAs(view);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Rail_field_click_while_open_selects_the_query_text() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var field = host.Named<Button>("RailSearchButton");
        Click(host, field);
        host.Window.KeyTextInput("auth");
        host.Settle();
        var label = host.Named<TextBlock>("RailSearchText").Text;
        var query = host.Named<TextBox>("SearchQueryBox");
        query.SelectionStart = query.SelectionEnd = query.Text!.Length;

        Click(host, field);

        await Assert.That(label).IsEqualTo("auth");
        await Assert.That(host.Vm.Search!.Query).IsEqualTo("auth");
        await Assert.That(host.Focused).IsSameReferenceAs(query);
        await Assert.That(query.SelectedText).IsEqualTo("auth");
    });

    /// ⌘K is bound everywhere. Off macOS Ctrl+K is too, and it yields while the terminal has focus,
    /// as Ctrl+R does; the command it wraps is the page's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Ctrl_k_yields_to_a_focused_terminal_off_mac() => AvaloniaSession.RunOnUiAsync(async () => {
        FakeDaemonClientService? service = null;
        using var host = new SearchWindowHost(workspaceFactory: id => NewWorkspace(service!, NewActions(service!).Actions, id));
        service = host.Service;
        var bindings = host.Window.KeyBindings.Where(k => k.Gesture is { Key: Key.K }).ToArray();
        var yield = new UnlessTerminalFocused(host.Window, m => m.OpenSearchCommand);

        await Assert.That(bindings.Single(k => k.Gesture!.KeyModifiers == KeyModifiers.Meta).Command).IsSameReferenceAs(host.Vm.OpenSearchCommand);
        await Assert.That(bindings.Any(k => k.Gesture!.KeyModifiers == KeyModifiers.Control)).IsEqualTo(!ShortcutLabels.UsesCommandKey);
        if (!ShortcutLabels.UsesCommandKey) {
            await Assert.That(bindings.Single(k => k.Gesture!.KeyModifiers == KeyModifiers.Control).Command).IsNotSameReferenceAs(host.Vm.OpenSearchCommand);
        }

        const string id = "0123456789abcdef0123456789abcdef";
        host.Vm.OpenSession(id);
        service.Agents.AddOrUpdate(WorkspaceFixtures.Agent(id, "claude", true, "/repo/myproj", sessionId: id));
        host.Settle();
        var workspace = (WorkspaceViewModel)host.Vm.CurrentWorkspace!;
        await Assert.That(yield.CanExecute(null)).IsTrue();
        workspace.ShowTerminalCommand.Execute().Subscribe();
        host.Settle();
        var terminal = host.Window.GetVisualDescendants().OfType<TerminalControl>().Single();
        await Assert.That(terminal.Focus()).IsTrue();

        var passedThrough = new KeyEventArgs { Key = Key.K, KeyModifiers = KeyModifiers.Control };
        new KeyBinding { Gesture = new KeyGesture(Key.K, KeyModifiers.Control), Command = yield }.TryHandle(passedThrough);

        await Assert.That(yield.CanExecute(null)).IsFalse();
        await Assert.That(passedThrough.Handled).IsFalse();
        await Assert.That(host.Vm.IsSearchOpen).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Search_menu_item_runs_the_open_search_command() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var windowMenu = new AppMenuBar(new RecordingOpener(), () => [], () => null).Build(host.Window)
            .Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window").Menu!;
        var items = windowMenu.Items.OfType<NativeMenuItem>().ToList();
        var search = items.Single(i => i.Header == "Search Sessions");

        await Assert.That(search.Command).IsSameReferenceAs(host.Vm.OpenSearchCommand);
        await Assert.That(search.Gesture).IsEqualTo(new KeyGesture(Key.K, KeyModifiers.Meta));
        await Assert.That(items.IndexOf(search)).IsEqualTo(items.FindIndex(i => i.Header == "Refresh") + 1);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task New_session_button_and_shortcut_run_new_session() => AvaloniaSession.RunOnUiAsync(async () => {
        using var host = new SearchWindowHost();
        var button = host.Named<Button>("RailNewSessionButton");
        var shortcuts = host.Window.KeyBindings.Where(k => k.Gesture is { Key: Key.N }).ToArray();

        await Assert.That(button.Command).IsSameReferenceAs(host.Vm.NewSessionCommand);
        await Assert.That(shortcuts.Length).IsEqualTo(2);
        await Assert.That(shortcuts.All(k => ReferenceEquals(k.Command, host.Vm.NewSessionCommand))).IsTrue();
    });
}

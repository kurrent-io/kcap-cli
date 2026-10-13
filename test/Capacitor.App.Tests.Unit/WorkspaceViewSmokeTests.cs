using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using SvcSystems.UI.Terminal;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the session workspace: WorkspaceView is a UserControl, so
/// each test hosts it inside a plain Window purely to give headless something to Show(). This
/// VIEW is
/// normally handed its DataContext through MainWindow's ContentControl/DataTemplate swap
/// (WorkspaceNavigationTests exercises that path); a smoke test instead sets DataContext directly,
/// bypassing the template so the view under test is exactly WorkspaceView, not MainWindow's swap
/// machinery.
///
/// WorkspaceViewModel always builds a real TerminalTabViewModel internally, which reaches
/// Dispatcher.UIThread.InvokeAsync on every daemon-cache dto push regardless of has_terminal (both
/// the NoTerminal and the attach branches dispatch) -- so every test here runs through the same
/// RunOnUiAsync nesting WorkspaceViewModelTests/WorkspaceNavigationTests use (DispatchAsync for a
/// live pumped dispatcher, WithImmediateRxScheduler so ObserveOn(RxSchedulers.MainThreadScheduler)
/// applies synchronously) and carries [NotInParallel("AvaloniaSession")].
public class WorkspaceViewSmokeTests {
    const string AgentId = "0123456789abcdef0123456789abcdef";

    static AgentStatusDto Agent(string id, bool? hasTerminal, string vendor = "claude") =>
        WorkspaceFixtures.Agent(id, vendor, hasTerminal, "/repo/myproj");

    static (WorkspaceView View, WorkspaceViewModel Vm, FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Attach) Build(
            string agentId = AgentId, Func<ITerminalSurface>? surface = null, IPlanArtifactSource? planArtifacts = null) {
        var daemon = new FakeDaemonClientService();
        var attach = new FakeTerminalAttachClientFactory();
        var vm = new WorkspaceViewModel(
            agentId, daemon, NewActions(), attach.Factory, surface ?? (() => new FakeTerminalSurface()),
            new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(),
            new ScriptedLocalControlOps(), new NoAttachmentUploader(), planArtifacts: planArtifacts);
        return (new WorkspaceView { DataContext = vm }, vm, daemon, attach);
    }

    static T? Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    /// A shown workspace on a PTY session, laid out at a real pane size — the shape every
    /// tab/focus test below starts from.
    static async Task<(Window Window, WorkspaceViewModel Vm, FakeDaemonClientService Daemon, FakeTerminalAttachClientFactory Attach)> ShowPtyAsync(
            Func<ITerminalSurface>? surface = null) {
        var (view, vm, daemon, attach) = Build(surface: surface);
        var window = new Window { Content = view, Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
        await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, vm, daemon, attach);
    }

    /// Effectively visible under this window. A name that never gets realized into the visual
    /// tree — a collapsed surface's own controls — reads as not visible rather than throwing.
    static bool Visible(Window window, string name) => Find<Control>(window, name) is { IsEffectivelyVisible: true };

    static bool IsOffscreen(Control control) =>
        Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control).IsOffscreen();

    /// Everything a real Tab from `start` reaches, in order, until it comes back round. Avalonia's
    /// own navigation handler is internal, so the ring is walked by pressing the key.
    static List<IInputElement> TabRing(Window window, Control start) {
        start.Focus();
        Dispatcher.UIThread.RunJobs();
        var ring = new List<IInputElement>();
        var seen = new HashSet<IInputElement>();
        while (window.FocusManager?.GetFocusedElement() is { } current && seen.Add(current)) {
            ring.Add(current);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }
        return ring;
    }

    /// Pins that every x:Name the view's code-behind and the suite reach for resolves before any
    /// dto arrives — the Terminal tab button and pane included, which are collapsed in this state
    /// and must still be in the tree for the code-behind to find. The chat surface's own names are
    /// resolved through its name scope, which holds whether or not it has been measured.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task WorkspaceView_resolves_all_named_controls() {
        await RunOnUiAsync(async () => {
            var (view, vm, _, _) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var names = new[] {
                "WorkspaceTitle", "WorkspaceSubtitle", "ChatTabButton",
                "TerminalTabButton", "StopButton", "SurfaceSwitch", "TerminalHost", "TerminalBanners",
                "DetachButton", "ReattachButton", "SessionEndedNote", "ChatHost", "WorkContextHost",
            };
            foreach (var name in names)
                await Assert.That(Find<Control>(window, name)).IsNotNull().Because($"{name} should resolve");
            await Assert.That(ToolTip.GetTip(Find<TextBlock>(window, "WorkspaceTitle")!))
                .IsEqualTo(vm.Title);

            var chatHost = Find<ChatTabView>(window, "ChatHost")!;
            foreach (var name in new[] { "ChatItems", "ChatPhaseNote", "ComposerInput", "SendButton" })
                await Assert.That(chatHost.FindControl<Control>(name)).IsNotNull().Because($"{name} should resolve");

            var pane = Find<WorkContextView>(window, "WorkContextHost")!;
            foreach (var name in new[] {
                "RefreshButton", "StaleDot", "StatePill", "WorkContextKey", "WorkContextTitle", "OverviewText", "PartOfLine", "PartsToggle", "PartsList",
                "BlockedByBlock", "CycleNoteText", "PhaseNoteText", "SignInButton", "RetryButton",
                "PullRequestSection", "PullRequestHeader", "PullRequestNumberMeta", "PullRequestCard", "LinkCards", "PullRequestToggle", "PullRequestEmptyText", "IssueSection",
                "PlanSection", "PlanToggle", "PlanHeaderText", "PlanCounts", "PlanBody", "PlanDocumentList", "PlanTaskList", "PlanInProgressBody", "PlanInProgressList",
                "SubagentsSection", "SubagentsToggle", "SubagentList", "RunningSubagentsBody", "RunningSubagentList",
                "WhoSection", "WhoToggle", "ContributorStack", "ContributorList", "WhoCountText", "RequesterRow", "RequesterName", "SessionToggle", "SessionFacts", "SessionIdButton", "OpenWorkItemButton", "PaneScroll",
            })
                await Assert.That(pane.FindControl<Control>(name)).IsNotNull().Because($"{name} should resolve");
            await Assert.That(pane.FindControl<ScrollViewer>("PaneScroll")!.HorizontalScrollBarVisibility)
                .IsEqualTo(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// The pane takes its fixed 320 and the terminal the rest, so the PTY size the terminal
    /// reports is the real center-pane width.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_is_320_wide_and_the_terminal_takes_the_remainder() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();

            try {
                var pane = Find<WorkContextView>(window, "WorkContextHost")!;
                var terminal = Find<TerminalControl>(window, "TerminalHost")!;
                await Assert.That(pane.Bounds.Width).IsEqualTo(320);
                await Assert.That(terminal.Bounds.Width).IsEqualTo(window.Bounds.Width - 320);
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                await vm.TeardownAsync();
            }
        });
    }

    /// Run-and-observe: drives ONE workspace through both has_terminal values for the same agent
    /// id. Chat is the default surface either way; the Chat/Terminal switch and the Terminal pane
    /// follow the PTY gate, and nothing stands in their place.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_is_always_offered_and_the_terminal_pair_follows_ShowsTerminalTab() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, _) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var chatButton = Find<Control>(window, "ChatTabButton")!;
            var chatHost = Find<Control>(window, "ChatHost")!;
            var tabButton = Find<Control>(window, "TerminalTabButton")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: false));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.ShowsTerminalTab).IsFalse();
            await Assert.That(vm.ShowsSurfaceSwitch).IsFalse();
            await Assert.That(chatHost.IsVisible).IsTrue(); // Chat is the default surface
            await Assert.That(tabButton.IsEffectivelyVisible).IsFalse();
            await Assert.That(terminalHost.IsVisible).IsFalse();
            await Assert.That(Find<Control>(window, "NoTerminalNote")).IsNull();

            // Same agent id, has_terminal flips to true: WorkspaceViewModel's ShowsTerminalTab is a
            // plain Rx projection off the daemon cache (not gated by TerminalTabViewModel's own
            // one-shot resolve CAS), so a later update still moves it.
            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.ShowsTerminalTab).IsTrue();
            await Assert.That(vm.ShowsSurfaceSwitch).IsTrue();
            await Assert.That(chatButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(tabButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(terminalHost.IsVisible).IsTrue();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Run-and-observe: drives the fake attach client's Result straight to AttachOutcome.Detached
    /// (TerminalTabViewModelTests' own idiom) and checks the view actually renders the combined
    /// Detached/Failed banner -- ReattachButton sits inside a Border whose OWN IsVisible is bound
    /// to the phase, so IsEffectivelyVisible (not IsVisible) is required to see the ancestor's
    /// collapse, same as MainWindowSmokeTests' shell-vs-workspace check.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Detached_state_shows_the_reattach_banner() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, attach) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var reattachButton = Find<Control>(window, "ReattachButton")!;
            var detachButton = Find<Control>(window, "DetachButton")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            await Assert.That(reattachButton.IsEffectivelyVisible).IsFalse();

            var client = attach.Created[^1];
            client.Result.SetResult(new AttachOutcome.Detached());
            await vm.Terminal.CurrentRunForTesting!;
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.Detached);
            await Assert.That(reattachButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(detachButton.IsEffectivelyVisible).IsFalse();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins that a normal read-write attach shows NO banner — one would overlay the terminal
    /// content. Read-only keeps its banner because it is the only explanation for dead
    /// keystrokes.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Read_write_attached_state_shows_no_banner() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, attach) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var detachButton = Find<Control>(window, "DetachButton")!;
            var bannerText = Find<TextBlock>(window, "AttachBannerText")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            var client = attach.Created[^1];
            await client.TriggerAttached([], reason: null);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.Terminal.State.ReadOnly).IsFalse();
            await Assert.That(detachButton.IsEffectivelyVisible).IsFalse();
            await Assert.That(bannerText.IsEffectivelyVisible).IsFalse();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Companion to the read-write test above: a read-only attach (TriggerAttached with a reason)
    /// is the ONE mode that shows the banner — warning copy with the daemon's reason, plus the
    /// Detach button (the only action a read-only session has).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Read_only_attached_state_shows_the_warning_banner_and_detach_button() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, attach) = Build();
            var window = new Window { Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var detachButton = Find<Control>(window, "DetachButton")!;
            var bannerText = Find<TextBlock>(window, "AttachBannerText")!;

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();

            var client = attach.Created[^1];
            await client.TriggerAttached([], reason: "review");
            Dispatcher.UIThread.RunJobs();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(vm.Terminal.State.ReadOnly).IsTrue();
            await Assert.That(detachButton.IsEffectivelyVisible).IsTrue();
            await Assert.That(bannerText.Text).IsEqualTo("Read-only: review");

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins the tab swap: Chat is the surface a PTY session opens on, and the terminal stays in
    /// the tree behind it — inert and reported offscreen, never unloaded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_opens_first_and_the_tabs_swap_the_surfaces_while_the_terminal_stays_in_the_tree() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            var chatHost = Find<Control>(window, "ChatHost")!;
            var banners = Find<Control>(window, "TerminalBanners")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            await Assert.That(vm.IsChatActive).IsTrue();
            await Assert.That(chatHost.IsEffectivelyVisible).IsTrue();
            await Assert.That(banners.IsEffectivelyVisible).IsFalse();
            await Assert.That(IsOffscreen(banners)).IsTrue();
            await Assert.That(terminalHost.IsEnabled).IsFalse();
            await Assert.That(terminalHost.IsHitTestVisible).IsFalse();
            await Assert.That(terminalHost.IsVisible).IsTrue();
            await Assert.That(terminalHost.Opacity).IsEqualTo(0.0);
            await Assert.That(IsOffscreen(terminalHost)).IsTrue();

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(chatHost.IsEffectivelyVisible).IsFalse();
            await Assert.That(IsOffscreen(chatHost)).IsTrue();
            await Assert.That(terminalHost.IsEnabled).IsTrue();
            await Assert.That(terminalHost.Opacity).IsEqualTo(1.0);
            await Assert.That(IsOffscreen(terminalHost)).IsFalse();
            await Assert.That(Find<Control>(window, "TerminalHost")).IsNotNull();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// A session with no PTY gets the whole chat surface — host, composer and Send, focused as the
    /// active tab — and its own end is announced through the composer hint, not the terminal
    /// banner layer, which stays off the Chat tab entirely.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_without_a_terminal_shows_the_chat_surface_and_ends_in_the_composer() {
        await RunOnUiAsync(async () => {
            var (view, vm, daemon, _) = Build();
            var window = new Window { Content = view, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: false));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var chatHost = Find<ChatTabView>(window, "ChatHost")!;
            await Assert.That(vm.IsChatActive).IsTrue();
            await Assert.That(chatHost.IsEffectivelyVisible).IsTrue();
            await Assert.That(Visible(window, "ComposerInput")).IsTrue();
            await Assert.That(Visible(window, "SendButton")).IsTrue();
            await Assert.That(chatHost.FindControl<TextBox>("ComposerInput")!.IsFocused).IsTrue();

            daemon.Agents.Remove(AgentId);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            await Assert.That(vm.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.SessionEnded);
            await Assert.That(Find<Control>(window, "TerminalBanners")!.IsEffectivelyVisible).IsFalse();
            await Assert.That(Find<Control>(window, "SessionEndedNote")!.IsEffectivelyVisible).IsFalse();
            await Assert.That(chatHost.IsEffectivelyVisible).IsTrue();
            await Assert.That(vm.Chat!.ComposerHint).IsEqualTo("This session has ended");

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// A launch the daemon has not published has no chat view model, and a chat surface bound to
    /// nothing draws every banner and the composer as empty shells: the starting panel stands
    /// alone until the first dto.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_starting_workspace_shows_the_starting_panel_and_no_chat_surface() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(AgentRow.FromPending(
                new PendingLaunchDto(AgentId, "claude", "/repo/myproj", "Fix the flaky test", DateTime.UtcNow, "spawned"),
                new RepoIdentity("path:/repo/myproj", "myproj")));
            var vm = new WorkspaceViewModel(
                AgentId, daemon, NewActions(), new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(),
                new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(),
                new ScriptedLocalControlOps(), new NoAttachmentUploader(), directory: directory);
            var window = new Window { Content = new WorkspaceView { DataContext = vm }, Width = 900, Height = 600 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var chatHost = Find<ChatTabView>(window, "ChatHost")!;
            await Assert.That(Visible(window, "StartingPanel")).IsTrue();
            await Assert.That(chatHost.IsVisible).IsFalse();
            await Assert.That(IsOffscreen(chatHost)).IsTrue();
            await Assert.That(Visible(window, "OpenInWebButton")).IsFalse();
            await Assert.That(Visible(window, "StopButton")).IsFalse();
            foreach (var name in new[] { "ChatActivityNote", "SubagentsBanner", "QueuedMessagesBanner", "ComposerCard", "ReadOnlyBanner", "SendButton" })
                await Assert.That(Visible(window, name)).IsFalse().Because($"{name} has nothing to show yet");

            daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: false));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            await Assert.That(Visible(window, "StartingPanel")).IsFalse();
            await Assert.That(Visible(window, "OpenInWebButton")).IsTrue();
            await Assert.That(Visible(window, "StopButton")).IsTrue();
            await Assert.That(chatHost.IsEffectivelyVisible).IsTrue();
            await Assert.That(Visible(window, "ComposerInput")).IsTrue();
            await Assert.That(Visible(window, "SubagentsBanner")).IsFalse();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins why the off-tab terminal is faded rather than collapsed: the PTY is sized from the
    /// laid-out pane, so opening on Chat must not hand the daemon the surface's ctor default.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_workspace_opened_on_chat_still_reports_the_laid_out_pane_size() {
        await RunOnUiAsync(async () => {
            XtermTerminalSurface? surface = null;
            var (window, vm, _, attach) = await ShowPtyAsync(surface: () => surface = new XtermTerminalSurface(80, 24));
            var client = attach.Created[^1];

            await Assert.That((client.Cols, client.Rows)).IsNotEqualTo((80, 24));
            await Assert.That((client.Cols, client.Rows)).IsEqualTo(surface!.CurrentSize);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins that focus follows the active tab, and that a terminal going live under the Chat tab
    /// does not steal the composer's focus. A Model assignment is that "went live" moment, so the
    /// test performs one rather than waiting for a reattach to produce it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Focus_follows_the_tab_and_survives_a_late_model_assignment() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            var composer = Find<TextBox>(window, "ComposerInput")!;
            var terminalHost = Find<TerminalControl>(window, "TerminalHost")!;
            await Assert.That(composer.IsFocused).IsTrue();

            terminalHost.Model = new XtermTerminalSurface(80, 24).Model;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(terminalHost.IsFocused).IsFalse();
            await Assert.That(composer.IsFocused).IsTrue();

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(terminalHost.IsFocused).IsTrue();

            await vm.ShowChatCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(composer.IsFocused).IsTrue();

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Pins that the inactive surface is out of the keyboard's reach in both directions — a Tab
    /// from either tab's own controls never lands on the other's.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tab_traversal_never_reaches_the_inactive_surface() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, attach) = await ShowPtyAsync();
            attach.Created[^1].Result.SetResult(new AttachOutcome.Detached());
            await vm.Terminal.CurrentRunForTesting!;
            Dispatcher.UIThread.RunJobs();

            var composer = Find<TextBox>(window, "ComposerInput")!;
            var detach = Find<Control>(window, "DetachButton")!;
            var reattach = Find<Control>(window, "ReattachButton")!;
            var send = Find<Control>(window, "SendButton")!;
            var terminalHost = Find<Control>(window, "TerminalHost")!;

            var ringFromComposer = TabRing(window, composer);
            await Assert.That(ringFromComposer).Contains(composer);
            await Assert.That(ringFromComposer).DoesNotContain(detach);
            await Assert.That(ringFromComposer).DoesNotContain(reattach);

            await vm.ShowTerminalCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            var ringFromTerminal = TabRing(window, terminalHost);
            await Assert.That(ringFromTerminal).Contains(terminalHost);
            await Assert.That(ringFromTerminal).DoesNotContain(composer);
            await Assert.That(ringFromTerminal).DoesNotContain(send);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }

    /// Fluent hover paints PART_ContentPresenter near-white; Stop must keep the danger colour.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Stop_keeps_danger_foreground_on_hover() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            try {
                var stop = Find<Button>(window, "StopButton")!;
                await Assert.That(stop.Classes.Contains("kcapDanger")).IsTrue();
                var centre = stop.TranslatePoint(new Point(stop.Bounds.Width / 2, stop.Bounds.Height / 2), window)!.Value;
                window.MouseMove(centre);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(stop.Classes.Contains(":pointerover")).IsTrue()
                    .Because("the hover must register for the assertion to mean anything");

                var presenter = stop.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                await Assert.That(ReferenceEquals(presenter.Foreground, window.FindResource("KcapDangerBrush"))).IsTrue();
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                await vm.TeardownAsync();
            }
        });
    }

    /// The header mark is the session status: a word, the same sentence for the screen reader
    /// and the tooltip's first line, and the extra fact only on hover.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Header_status_names_working_idle_and_needs_you() {
        await RunOnUiAsync(async () => {
            var (window, vm, daemon, _) = await ShowPtyAsync();
            try {
                daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true) with { AwaitingInput = false });
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var working = StatusMark(window);
                await Assert.That(working.IsEffectivelyVisible).IsTrue();
                var workingWord = working.FindControl<TextBlock>("StatusWord")!;
                await Assert.That(workingWord.Text).IsEqualTo("Working");
                await Assert.That(workingWord.Foreground).IsSameReferenceAs(window.FindResource("KcapPurpleBrush"));
                await Assert.That(workingWord.Bounds.Width).IsGreaterThan(workingWord.Bounds.Height);
                var path = Find<TextBlock>(window, "WorkspaceSubtitle")!;
                double LeftOf(Control control) => control.TranslatePoint(default, window)!.Value.X;
                double RightOf(Control control) => control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
                await Assert.That(RightOf(workingWord)).IsLessThanOrEqualTo(RightOf(working) + 1);
                await Assert.That(LeftOf(path)).IsGreaterThanOrEqualTo(RightOf(working) - 1);
                var workingLines = TipLines(working);
                await Assert.That(workingLines[0]).StartsWith("Working for ");
                await Assert.That(workingLines[1]).IsEqualTo("Status");
                await Assert.That(AutomationProperties.GetName(working)).IsEqualTo(workingLines[0]);
                await Assert.That(Visible(window, "ChatActivityNote")).IsFalse();

                daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true) with { AwaitingInput = true });
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await AssertStatus(window, "Idle", "Idle", window.FindResource("KcapWarningBrush")!);
                var idleMark = StatusMark(window);
                var idleWord = idleMark.FindControl<TextBlock>("StatusWord")!;
                var subtitle = Find<TextBlock>(window, "WorkspaceSubtitle")!;
                await Assert.That(idleWord.FontSize).IsEqualTo(subtitle.FontSize);
                await Assert.That(idleWord.FontWeight).IsEqualTo(subtitle.FontWeight);
                await Assert.That(double.IsNaN(idleWord.LineHeight)).IsTrue();
                await Assert.That(double.IsNaN(subtitle.LineHeight)).IsTrue();
                double Mid(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height / 2), window)!.Value.Y;
                double Baseline(TextBlock text) => text.TranslatePoint(new Point(0, text.TextLayout.Baseline), window)!.Value.Y;
                var glyph = idleMark.FindControl<Panel>("Glyph")!;
                var title = Find<TextBlock>(window, "WorkspaceTitle")!;
                double Bottom(Control control) => control.TranslatePoint(new Point(0, control.Bounds.Height), window)!.Value.Y;
                double Top(Control control) => control.TranslatePoint(default, window)!.Value.Y;
                await Assert.That(Top(subtitle) - Bottom(title)).IsGreaterThan(4);
                await Assert.That(Math.Abs(Mid(idleWord) - Mid(subtitle))).IsLessThan(2);
                await Assert.That(Math.Abs(Baseline(idleWord) - Baseline(subtitle))).IsLessThan(1);
                var capCentre = Baseline(idleWord) - idleWord.FontSize * 0.36;
                await Assert.That(Math.Abs(Mid(glyph) - capCentre)).IsLessThan(1);

                var limit = new UsageLimitNoticeDto(
                    UsageLimitKinds.Blocked, "Weekly limit reached", "Pick one", [new UsageLimitOptionDto(1, "Stop")]);
                daemon.Agents.AddOrUpdate(Agent(AgentId, hasTerminal: true) with { AwaitingInput = false, UsageLimit = limit });
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await AssertStatus(window, "Needs you", "Needs you", window.FindResource("KcapWarningBrush")!);
                var needsYou = string.Join('\n', TipLines(StatusMark(window)));
                await Assert.That(needsYou).Contains("Weekly limit reached");
                await Assert.That(needsYou).Contains("Usage limit");
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                await vm.TeardownAsync();
            }
        });
    }

    static AgentStatusMark StatusMark(Window window) =>
        window.GetVisualDescendants().OfType<AgentStatusMark>().Single(mark => mark.Name == "WorkspaceStatus");

    static async Task AssertStatus(Window window, string word, string accessibleName, object brush) {
        var mark = StatusMark(window);
        await Assert.That(mark.IsEffectivelyVisible).IsTrue();
        var text = mark.FindControl<TextBlock>("StatusWord")!;
        await Assert.That(text.Text).IsEqualTo(word);
        await Assert.That(text.Foreground).IsSameReferenceAs(brush);
        await Assert.That(AutomationProperties.GetName(mark)).IsEqualTo(accessibleName);
        await Assert.That(TipLines(mark)[0]).IsEqualTo(accessibleName);
    }

    static string[] TipLines(Control control) {
        ToolTip.SetIsOpen(control, true);
        Dispatcher.UIThread.RunJobs();
        var tip = (Control)ToolTip.GetTip(control)!;
        tip.UpdateLayout();
        var lines = tip.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible)
            .Select(t => t.Text ?? "")
            .ToArray();
        ToolTip.SetIsOpen(control, false);
        return lines;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_artefacts_position_shows_with_a_document_and_hosts_the_list_and_reader() {
        await RunOnUiAsync(async () => {
            var source = new FakePlanArtifactSource();
            source.Enqueue(Ready(Doc("docs/x-design.md") with { Source = "discovered" }));
            var (view, vm, daemon, _) = Build(planArtifacts: source);
            var window = new Window { Content = view, Width = 1000, Height = 640 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(Find<Button>(window, "ArtefactsTabButton")!.IsEffectivelyVisible).IsFalse();

            daemon.Agents.AddOrUpdate(WorkspaceFixtures.Agent(AgentId, "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: AgentId));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(Find<Button>(window, "ArtefactsTabButton")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(Find<ContentControl>(window, "ArtefactsHost")!.Content).IsNull();

            await vm.ShowArtefactsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(Find<ContentControl>(window, "ArtefactsHost")!.Content).IsTypeOf<ArtefactsView>();
            await Assert.That(Find<ItemsControl>(window, "DocumentList")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(Find<ContentControl>(window, "DocumentReaderHost")!.IsEffectivelyVisible).IsTrue();

            await vm.Artefacts.SelectCommand.Execute(vm.Artefacts.Documents[0]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Assert.That(Find<ScrollViewer>(window, "DocumentScroll")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(Find<Border>(window, "DocumentNotice")!.IsEffectivelyVisible).IsFalse();

            window.Close();
            await vm.TeardownAsync();
        });
    }

    /// The header's right side is icons: the segment is what grows.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_in_web_is_an_icon_button_with_a_tooltip() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();
            var button = Find<Button>(window, "OpenInWebButton")!;
            await Assert.That(button.Content).IsNotTypeOf<string>();
            await Assert.That(ToolTip.GetTip(button)).IsEqualTo("Open in web");
            await Assert.That(button.Classes.Contains("kcapIcon")).IsTrue();
            window.Close();
            await vm.TeardownAsync();
        });
    }
}

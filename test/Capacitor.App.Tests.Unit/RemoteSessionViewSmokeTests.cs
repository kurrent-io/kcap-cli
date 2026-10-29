using System.Reactive.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.RemoteFixtures;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the remote host: the card templates it shares with the chat
/// pane resolve here too, so a card raised on another machine is answerable in this window.
/// These run under the real Avalonia scheduler (AvaloniaSession.DispatchAsync, not the immediate
/// pin): SessionAccessService publishes from a thread-pool continuation, and only the real
/// scheduler marshals that onto the UI thread before it reaches a binding.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteSessionViewSmokeTests {
    [Test]
    public async Task An_unresolved_server_attempt_can_be_discarded_without_sending_a_prompt() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                var id = Guid.NewGuid();
                host.Lane.PendingInputSubject.OnNext(new("s1", [new QueuedInputItem { DispatchId = id, Text = "old attempt" }]));
                host.Settle();
                var discard = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscardQueuedButton" && b.IsVisible);
                var point = discard.TranslatePoint(new Point(discard.Bounds.Width / 2, discard.Bounds.Height / 2), host.Window)!.Value;
                host.Window.MouseDown(point, MouseButton.Left);
                host.Window.MouseUp(point, MouseButton.Left);
                await host.SettleUntilAsync(() => host.Vm.Chat.QueuedMessages.Count == 0, "discarded");
                await Assert.That(host.Lane.UserInputs).IsEmpty();
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    sealed class Host : IDisposable {
        readonly SessionAccessService _access;
        readonly FakeAgentDirectory _directory = new();

        public readonly FakeServerLane Lane = new();
        public readonly FakePermissionService Permissions = new();
        public readonly FakeTimeProvider Time = new();
        public readonly FakeRemoteAgents Remote = new();
        public readonly RemoteSessionViewModel Vm;
        public readonly RemoteSessionView View;
        public readonly Window Window;

        public Host(IPlanArtifactSource? planArtifacts = null, string sessionId = "s1", IWindowImpl? windowImpl = null, bool realTerminal = false) {
            _access = new SessionAccessService(Lane, Time);
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
            var row = AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = sessionId, Status = "Running", DaemonName = "work-mac",
                Vendor = "claude", OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
            });
            _directory.Rows.AddOrUpdate(row);
            Vm = new RemoteSessionViewModel(row, _directory, _access, Permissions, NewActions(), Lane,
                (_, _) => Task.FromResult(new SessionDetailFetch(Detail())), new RecordingOpener(), Time, new FakeWorkContextSource(),
                () => realTerminal ? new XtermTerminalSurface(130, 54) : new FakeTerminalSurface(), planArtifacts,
                remoteAgents: Remote, notifier: new RecordingNotifier());
            View = new RemoteSessionView { DataContext = Vm };
            Window = windowImpl is null ? new Window() : new Window(windowImpl);
            Window.Content = View;
            Window.Width = 900;
            Window.Height = 700;
            Window.Show();
            Settle();
        }

        public void Settle() {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public async Task SettleUntilAsync(Func<bool> condition, string what) {
            await WaitUntilAsync(() => { Settle(); return condition(); }, what: what);
            Settle();
        }

        public void SetStatus(string status) {
            var row = _directory.Rows.Items.Single();
            _directory.Rows.AddOrUpdate(row with { Status = status });
            Settle();
        }

        public void Dispose() {
            Window.Close();
            _access.Dispose();
            Permissions.Dispose();
            _directory.Dispose();
            Remote.Dispose();
        }
    }

    [Test]
    public async Task A_remote_terminal_scrolls_locally_and_sends_keyboard_and_button_arrows() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host(realTerminal: true);
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                await host.Vm.ShowTerminalCommand.Execute();
                await host.SettleUntilAsync(() => host.Vm.Terminal!.Phase == RemoteTerminalPhase.Live, "live terminal");
                host.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 130, 54));
                var feed = string.Concat(Enumerable.Range(0, 120).Select(i => $"line {i}\r\n"))
                    + "prompt\x1b[?1h";
                host.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(feed))));
                host.Window.Height = 450;
                host.Settle();

                var terminal = host.View.FindControl<RemoteTerminalControl>("TerminalHost")!;
                var viewport = host.View.FindControl<RemoteTerminalViewport>("TerminalViewport")!;
                var model = terminal.Model!;
                await Assert.That(viewport.Extent.Height).IsGreaterThan(viewport.Viewport.Height);
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);
                var point = viewport.TranslatePoint(new Point(30, 30), host.Window)!.Value;
                var beforeWheel = viewport.Offset.Y;
                host.Window.MouseWheel(point, new Vector(0, 0.1));
                host.Settle();
                await Assert.That(viewport.Offset.Y).IsLessThan(beforeWheel);
                var afterSmallWheel = viewport.Offset.Y;
                host.Window.MouseWheel(point, new Vector(0, 100));
                host.Settle();
                await Assert.That(afterSmallWheel - viewport.Offset.Y).IsLessThanOrEqualTo(9 * terminal.CellHeight);
                for (var i = 0; i < 40; i++) { host.Window.MouseWheel(point, new Vector(0, 100)); host.Settle(); }
                await Assert.That(viewport.Offset.Y).IsEqualTo(0);
                await Assert.That(model.ScrollOffset).IsLessThan(model.MaxScrollback);
                var beforeLargeWheel = model.ScrollOffset;
                host.Window.MouseWheel(point, new Vector(0, -100));
                host.Settle();
                await Assert.That(model.ScrollOffset - beforeLargeWheel).IsLessThanOrEqualTo(9);
                for (var i = 0; i < 40; i++) { host.Window.MouseWheel(point, new Vector(0, -100)); host.Settle(); }
                await Assert.That(model.ScrollOffset).IsEqualTo(model.MaxScrollback);
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);

                host.Window.MouseWheel(point, new Vector(0, 100));
                host.Settle();
                terminal.Focus();
                host.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                host.Window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                host.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                host.Window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 2, "keyboard arrows");
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);
                foreach (var label in new[] { "↑", "↓" }) {
                    host.Window.MouseWheel(point, new Vector(0, 100));
                    host.Settle();
                    var button = host.View.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
                    var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
                    host.Window.MouseDown(center, MouseButton.Left);
                    host.Window.MouseUp(center, MouseButton.Left);
                    host.Settle();
                }
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 4, "button arrows");
                await Assert.That(terminal.IsKeyboardFocusWithin).IsTrue();
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);
                await Assert.That(host.Lane.SpecialKeys).IsEquivalentTo(new[] {
                    ("a1", SpecialKeys.ArrowUp), ("a1", SpecialKeys.ArrowDown),
                    ("a1", SpecialKeys.ArrowUp), ("a1", SpecialKeys.ArrowDown),
                });
                await Assert.That((model.Terminal.Cols, model.Terminal.Rows)).IsEqualTo((130, 54));
            } finally {
                await host.Vm.TeardownAsync();
            }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_mouse_tracking_app_receives_proportional_wheel_input_in_either_buffer(bool alternate) {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host(realTerminal: true);
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                await host.Vm.ShowTerminalCommand.Execute();
                await host.SettleUntilAsync(() => host.Vm.Terminal!.Phase == RemoteTerminalPhase.Live, "live terminal");
                host.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 103, 38));
                var feed = string.Concat(Enumerable.Range(0, 90).Select(i => $"redraw {i}\r\n"));
                feed += (alternate ? "\x1b[?1049h" : "") + "\x1b[?1000h\x1b[?1006hfull-screen conversation";
                var bytes = System.Text.Encoding.UTF8.GetBytes(feed);
                host.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Convert.ToBase64String(bytes)));
                host.Settle();
                var viewport = host.View.FindControl<RemoteTerminalViewport>("TerminalViewport")!;
                var terminal = host.View.FindControl<RemoteTerminalControl>("TerminalHost")!;
                var scrollback = terminal.Model!.ScrollOffset;
                var point = terminal.TranslatePoint(new Point(30, 30), host.Window)!.Value;
                for (var i = 0; i < 10; i++) host.Window.MouseWheel(point, new Vector(0, 0.1));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 1, "one accumulated wheel step");
                host.Window.MouseWheel(point, new Vector(0, 100.25));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 7, "a large event is capped at six reports");
                host.Window.MouseWheel(point, new Vector(0, 0.75));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 8, "fractional remainder survives the cap");
                await Assert.That(viewport.HorizontalScrollBarVisibility).IsEqualTo(ScrollBarVisibility.Hidden);
                await Assert.That(viewport.VerticalScrollBarVisibility).IsEqualTo(ScrollBarVisibility.Hidden);
                foreach (var (_, key) in host.Lane.SpecialKeys) {
                    await Assert.That(TerminalScrollKey.TryDecode(key, out var report)).IsTrue();
                    await Assert.That(report.AsSpan().StartsWith("\x1b[<64;"u8)).IsTrue();
                }
                await Assert.That(terminal.Model.ScrollOffset).IsEqualTo(scrollback);
                host.Window.Height = 450;
                host.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 100, 30));
                host.Settle();
                host.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1",
                    Convert.ToBase64String("\x1b[2J\x1b[Hredrawn history\x1b[30;1Hdraft"u8.ToArray())));
                host.Settle();
                point = terminal.TranslatePoint(new Point(30, 30), host.Window)!.Value;
                var before = terminal.Model.ScrollOffset;
                host.Window.MouseWheel(point, new Vector(0, -0.5));
                host.Window.MouseWheel(point, new Vector(0, -0.5));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 9, "one downward step after resize");
                await Assert.That(TerminalScrollKey.TryDecode(host.Lane.SpecialKeys[^1].Key, out var down)).IsTrue();
                await Assert.That(down.AsSpan().StartsWith("\x1b[<65;"u8)).IsTrue();
                await Assert.That(terminal.Model.ScrollOffset).IsEqualTo(before);
                var buffer = terminal.Model.Terminal.Buffer;
                await Assert.That(buffer.GetLine(buffer.BaseY)!.TranslateToString(true)).IsEqualTo("redrawn history");
                await Assert.That(buffer.GetLine(buffer.BaseY + 29)!.TranslateToString(true)).IsEqualTo("draft");
            } finally {
                await host.Vm.TeardownAsync();
            }
        });
    }

    // Forward to the real headless platform so these assertions exercise hit testing and routed
    // input, while recording the move request that a native window would receive.
    public class MoveRecorder : DispatchProxy {
        public IWindowImpl Target { get; set; } = null!;
        public int Moves { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (targetMethod!.Name == "BeginMoveDrag") Moves++;
            return targetMethod.Invoke(Target, args);
        }
    }

    [Test]
    public async Task The_remote_header_drags_beside_work_context_and_keeps_tab_buttons_clickable() {
        await AvaloniaSession.DispatchAsync(async () => {
            var platformWindow = new Window();
            var platform = DispatchProxy.Create<IWindowImpl, MoveRecorder>();
            var recorder = (MoveRecorder)platform;
            recorder.Target = platformWindow.PlatformImpl!;
            var closed = typeof(ITopLevelImpl).GetProperty("Closed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var helperClosed = closed.GetValue(recorder.Target);
            try {
                using var host = new Host(windowImpl: platform);
                try {
                    await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                    await Assert.That(host.View.GetVisualDescendants().OfType<WorkContextView>().Single().DataContext)
                    .IsSameReferenceAs(host.Vm.WorkContext);

                    // Blank padding needs a hit-testable background; title text must bubble too.
                    var title = host.View.FindControl<SessionTitleEditor>("RemoteTitle")!.FindControl<TextBlock>("TitleText")!;
                    var titlePoint = title.TranslatePoint(new Point(4, title.Bounds.Height / 2), host.Window)!.Value;
                    foreach (var point in new[] { new Point(200, 8), titlePoint }) {
                        platformWindow.MouseDown(point, MouseButton.Left);
                        platformWindow.MouseUp(point, MouseButton.Left);
                    }
                    await Assert.That(recorder.Moves).IsEqualTo(2);

                    platformWindow.MouseDown(new Point(200, 8), MouseButton.Right);
                    platformWindow.MouseUp(new Point(200, 8), MouseButton.Right);
                    await Assert.That(recorder.Moves).IsEqualTo(2);

                    var terminal = host.View.FindControl<Button>("TerminalTabButton")!;
                    var buttonPoint = terminal.TranslatePoint(new Point(terminal.Bounds.Width / 2, terminal.Bounds.Height / 2), host.Window)!.Value;
                    platformWindow.MouseDown(buttonPoint, MouseButton.Left);
                    platformWindow.MouseUp(buttonPoint, MouseButton.Left);
                    host.Settle();
                    await Assert.That(host.Vm.IsTerminalActive).IsTrue();
                    await Assert.That(recorder.Moves).IsEqualTo(2);
                } finally {
                    await host.Vm.TeardownAsync();
                }
            } finally {
                // Host replaces the shared implementation's close callback. Restore the helper's
                // callback after Host closes so its managed subscriptions are released too.
                closed.SetValue(recorder.Target, helperClosed);
                platformWindow.Close();
            }
            await Assert.That(platformWindow.PlatformImpl).IsNull();
            return true;
        });
    }

    [Test]
    public async Task An_acp_question_renders_in_the_chat_pane_through_the_shared_card_template() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(chat.IsVisible).IsTrue();

            var card = PendingPermissionRequest.FromServer(
                new ServerElicitationRequest("s1", "q1", "Pick a branch",
                    [new() { OptionId = "a", Label = "main" }, new() { OptionId = "b", Label = "next" }], false),
                DateTimeOffset.UtcNow);
            card.AgentId = "a1";
            host.Permissions.Add(card);
            await host.SettleUntilAsync(() => host.Vm.Chat.Cards.HasPendingCards, "the card");

            await Assert.That(chat.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "AcpQuestionCard")).IsTrue();
            var options = chat.GetVisualDescendants().OfType<Button>()
                .Where(b => b is not ToggleButton && b.Classes.Contains("acpOption"))
                .ToList();
            var labels = options.Select(b => ((TextBlock)b.Content!).Text ?? "").ToList();
            await Assert.That(labels).IsEquivalentTo(new[] { "main", "next" });
            // A long option has to wrap in the stretched row rather than run off its edge.
            await Assert.That(options.Select(b => ((TextBlock)b.Content!).TextWrapping).Distinct())
                .IsEquivalentTo(new[] { TextWrapping.Wrap });

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    /// Denied is a stated verdict, not an empty pane: the chat goes away and the banner says why.
    [Test]
    public async Task A_denied_session_replaces_the_chat_with_the_access_note() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");

            var banner = host.View.FindControl<Border>("AccessBanner")!;
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(chat.IsVisible).IsTrue();

            host.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            host.Lane.SessionAccessChangedSubject.OnNext("s1");
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Denied, "denied");
            await Assert.That(host.View.GetVisualDescendants().OfType<WorkContextView>().Single().IsVisible).IsFalse();

            await Assert.That(chat.IsVisible).IsFalse();
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(host.View.FindControl<TextBlock>("AccessNoteText")!.Text)
                .IsEqualTo("You no longer have access to this session");

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    [Test]
    public async Task A_remote_running_turn_without_turn_details_can_be_interrupted_explicitly_until_idle_or_disconnected() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
                var composer = chat.FindControl<TextBox>("ComposerInput")!;
                var interrupt = chat.FindControl<Button>("InterruptButton")!;
                await Assert.That(host.Vm.Chat.AgentStatus.IsRunningUnknownTurn).IsTrue();
                await Assert.That(interrupt.IsEffectivelyVisible).IsTrue();
                await Assert.That(interrupt.IsEffectivelyEnabled).IsTrue();

                composer.Focus();
                host.Window.KeyTextInput("keep this draft");
                host.Settle();
                // Unknown turn state keeps the explicit action, but a casual Escape must not reach the PTY.
                host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                host.Settle();
                await Assert.That(host.Lane.SpecialKeys).IsEmpty();
                await Assert.That(interrupt.Content).IsEqualTo("Interrupt");
                var point = interrupt.TranslatePoint(new Point(interrupt.Bounds.Width / 2, interrupt.Bounds.Height / 2), host.Window)!.Value;
                host.Window.MouseDown(point, MouseButton.Left);
                host.Window.MouseUp(point, MouseButton.Left);
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 1, "interrupt delivered");
                await Assert.That(host.Lane.SpecialKeys).IsEquivalentTo(new[] { ("a1", SpecialKeys.Escape) });
                await Assert.That(composer.Text).IsEqualTo("keep this draft");
                await Assert.That(host.Vm.Chat.ComposerText).IsEqualTo("keep this draft");

                host.SetStatus("Idle");
                await Assert.That(interrupt.IsVisible).IsFalse();
                await Assert.That(await host.Vm.Chat.InterruptCommand.CanExecute.FirstAsync()).IsFalse();
                composer.Focus();
                host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                host.Settle();
                await Assert.That(host.Lane.SpecialKeys).Count().IsEqualTo(1);

                host.SetStatus("Running");
                await Assert.That(interrupt.IsEffectivelyVisible).IsTrue();
                await Assert.That(await host.Vm.Chat.InterruptCommand.CanExecute.FirstAsync()).IsTrue();
                host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
                host.Settle();
                await Assert.That(host.Vm.Chat.ShowsInterrupt).IsFalse();
                await Assert.That(await host.Vm.Chat.InterruptCommand.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(host.Lane.SpecialKeys).Count().IsEqualTo(1);
                await Assert.That(host.Vm.Chat.ComposerText).IsEqualTo("keep this draft");
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    /// The tab strip swaps the panes, and the terminal tab is offered only for a PTY harness.
    [Test]
    public async Task The_terminal_tab_swaps_the_pane_and_offers_the_special_keys() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            var terminalTab = host.View.FindControl<Button>("TerminalTabButton")!;
            var terminalHost = host.View.FindControl<Control>("TerminalPane")!;
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(terminalTab.IsVisible).IsTrue();
            await Assert.That(terminalHost.IsVisible).IsFalse();

            await host.Vm.ShowTerminalCommand.Execute();
            await host.SettleUntilAsync(() => terminalHost.IsVisible, "the terminal pane");
            await Assert.That(chat.IsVisible).IsFalse();
            var keys = host.View.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("specialKey")).Select(b => b.Content as string).ToList();
            await Assert.That(keys).Contains("Esc");
            await Assert.That(keys).Contains("Ctrl+C");

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    [Test]
    public async Task The_artefacts_tab_hosts_the_list_and_reader_and_open_in_web_is_an_icon() {
        await AvaloniaSession.DispatchAsync(async () => {
            var source = new FakePlanArtifactSource();
            source.Default = Ready(Doc("docs/x-design.md"));
            using var host = new Host(source, DocumentSession);
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            await (host.Vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            host.Settle();
            var tab = host.View.FindControl<Button>("ArtefactsTabButton")!;
            await Assert.That(tab.IsEffectivelyVisible).IsTrue();
            await Assert.That(host.View.FindControl<ContentControl>("ArtefactsPane")!.Content).IsNull();

            await host.Vm.ShowArtefactsCommand.Execute();
            host.Settle();
            await Assert.That(host.View.FindControl<ContentControl>("ArtefactsPane")!.Content).IsTypeOf<ArtefactsView>();
            var open = host.View.FindControl<Button>("OpenInWebButton")!;
            await Assert.That(ToolTip.GetTip(open)).IsEqualTo("Open in web");
            await Assert.That(open.Content).IsNotTypeOf<string>();

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    /// The owner's header offers the pencil; it swaps the title for the field and the ✓ / ✗ pair,
    /// and a save lands the server's title back in the header.
    [Test]
    public async Task The_pencil_toggles_the_editor_and_a_save_shows_the_new_title() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            host.Remote.Rename = (_, _, title) => Task.FromResult(new SessionRenameResult(true, title, null));
            var editor = host.View.FindControl<SessionTitleEditor>("RemoteTitle")!;
            var pencil = editor.FindControl<Button>("RenameButton")!;
            var input = editor.FindControl<TextBox>("TitleInput")!;
            await Assert.That(pencil.IsEffectivelyVisible).IsTrue();
            await Assert.That(input.IsEffectivelyVisible).IsFalse();

            pencil.Command!.Execute(null);
            await host.SettleUntilAsync(() => input.IsEffectivelyVisible, "the field");
            await Assert.That(pencil.IsEffectivelyVisible).IsFalse();
            await Assert.That(editor.FindControl<Button>("SaveRenameButton")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(editor.FindControl<Button>("CancelRenameButton")!.IsEffectivelyVisible).IsTrue();

            input.Text = "New name";
            editor.FindControl<Button>("SaveRenameButton")!.Command!.Execute(null);
            await host.SettleUntilAsync(() => pencil.IsEffectivelyVisible, "the pencil back");
            await Assert.That(editor.FindControl<TextBlock>("TitleText")!.Text).IsEqualTo("New name");

            await host.Vm.TeardownAsync();
            return true;
        });
    }
}

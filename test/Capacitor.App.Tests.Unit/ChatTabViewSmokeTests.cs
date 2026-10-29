using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Subjects;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the Chat tab on its own: the view is hosted directly with a
/// ChatTabViewModel DataContext, so what is under test is exactly ChatTabView's list virtualization
/// and follow-tail, not WorkspaceView's tab swap. Same session rules as every UI suite here —
/// RunOnUiAsync plus [NotInParallel("AvaloniaSession")].
public class ChatTabViewSmokeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLinkLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"See [docs](https://example.com/docs) now."}]}}""";
    const string ToolCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
    const string ThinkingLine = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"weighing it"}]}}""";
    const string ToolResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string PublishCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_pub","name":"mcp__plugin_kcap_kcap-artefacts__publish_artefact","input":{"title":"Retention brief","html":"<p>x</p>"}}]}}""";
    const string PublishResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_pub","content":"{\"artefact\":{\"artefact_id\":\"01ec\",\"title\":\"Retention brief\",\"owner_user_id\":\"u1\",\"visibility\":\"org\",\"latest_version\":1,\"updated_at\":\"2026-10-07T10:00:00Z\",\"is_owner\":true,\"url\":\"https://kurrent.kcap.ai/artefacts/01ec\"}}"}]}}""";
    const string ToolErrorLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"boom","is_error":true}]}}""";
    static readonly TimeSpan CrDelay = TimeSpan.FromMilliseconds(150);
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string ReadResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}}""";
    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string AgentLaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string AgentFinishLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}}""";

    static string CallLine(int n) => ToolCallLine.Replace("\"t1\"", $"\"t{n}\"");
    static string ResultLine(int n) => ToolResultLine.Replace("\"t1\"", $"\"t{n}\"");

    // The short tail leaves its preceding tall reply unmeasured until the reader scrolls up.
    static string[] UnevenHistory() {
        var prose = string.Join("\\n\\n", Enumerable.Repeat("A longer reply whose paragraphs wrap across the chat column.", 20));
        var tall = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + prose + "\"}]}}";
        return Enumerable.Range(0, 60).SelectMany(_ => new[] { tall, UserLine, UserLine, UserLine, UserLine, UserLine })
            .Concat([UserLine, UserLine]).ToArray();
    }

    static List<Control> ToolRows(ChatTabView view) => view.GetVisualDescendants().OfType<Control>()
        .Where(c => c.Name == "ToolCallRow" && c.DataContext is ToolCallItem).ToList();
    static Button Summary(ChatTabView view) => view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("toolSummary") && b.DataContext is ToolGroupItem);
    static Button? SummaryOrNull(ChatTabView view) => view.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("toolSummary") && b.DataContext is ToolGroupItem);
    static ToolGroupItem OnlyGroup(Host host) => host.Chat.Items.OfType<ToolGroupItem>().Single();

    /// A synthetic pointer event hit-tests the compositor's last committed scene, which layout alone
    /// does not refresh: a control shown since the last frame is invisible to the click until the
    /// render timer ticks. One tick is enough on macOS/Linux; Windows headless sometimes needs a
    /// second before a freshly-shown summary button is in the scene (tool groups nest under a Border).
    /// Aim at the control's center — a (2,2) corner miss is easy when DPI scales.
    static Point PresentAndLocate(Host host, Control target) {
        host.Settle();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        host.Window.UpdateLayout();
        if (target.Bounds.Width < 1 || target.Bounds.Height < 1)
            throw new InvalidOperationException(
                $"Click target '{target.GetType().Name}' has empty bounds after present; cannot hit-test.");
        var local = new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
        return target.TranslatePoint(local, host.Window)
            ?? throw new InvalidOperationException("Click target is not under the window.");
    }

    static void Click(Host host, Control target) {
        var origin = PresentAndLocate(host, target);
        host.Window.MouseDown(origin, MouseButton.Left);
        host.Window.MouseUp(origin, MouseButton.Left);
        host.Settle();
    }

    /// A wheel gesture over the list, big enough to reach the top. The reader's own scrolling has to
    /// arrive as input: follow-tail tells the reader apart from layout by the gesture, so a bare
    /// Offset assignment reads as the panel's doing and is followed.
    static void WheelUp(Host host) {
        var list = host.View.FindControl<ItemsControl>("ChatItems")!;
        var center = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseWheel(center, new Vector(0, 100_000));
    }

    sealed class Host {
        bool _shown;
        readonly ReplaySubject<AgentPresence> _presence = new(1);

        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeTerminalAttachClientFactory Attach { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public SessionRuns Runs { get; }
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }
        public ChatTabView View { get; }
        public Window Window { get; }
        public ScrollViewer Scroll => View.GetVisualDescendants().OfType<ScrollViewer>().First();
        public bool HasScroll => View.GetVisualDescendants().OfType<ScrollViewer>().Any();
        public TextBox Composer => View.FindControl<TextBox>("ComposerInput")!;

        /// `show: false` leaves the window unshown, so the view has no template and no
        /// ScrollViewer until Show() is called — the order production takes, where the tab's
        /// first read starts before the workspace view exists.
        public Host(bool show = true, IObservable<string?>? sessionId = null, IObservable<Capacitor.Cli.Core.Accounts.AccountsSnapshotDto?>? accountReadings = null) {
            Runs = new SessionRuns(Time);
            Terminal = new TerminalTabViewModel("a1", Daemon, Attach.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel(
                "a1", Daemon, new TerminalChatInput(Terminal, "a1", Daemon, new ScriptedLocalControlOps(), _presence), new NoAttachmentUploader(), TranscriptChat.For("claude"), Opener, Time, Permissions, Runs,
                sessionId: sessionId, localDaemonOnAppServer: Observable.Return(true),
                accountReadings: accountReadings, usageClock: Observable.Return(StripReadings.Now));
            View = new ChatTabView { DataContext = Chat };
            Window = new Window { Content = View, Width = 800, Height = 600 };
            if (!show) return;
            Show();
        }

        public void Show() {
            Window.Show();
            _shown = true;
            Settle();
        }

        public void Settle() {
            Dispatcher.UIThread.RunJobs();
            if (_shown) Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// The rest of what the composer needs before it takes attachments: a daemon advertising
        /// the capability, over a session working in its own worktree.
        public void AllowAttachments() {
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/2"]));
            _presence.OnNext(new AgentPresence(Agent("a1", "claude", hasTerminal: true, workLocation: WorkLocationText.Owned), false));
            Settle();
        }

        /// A loaded transcript over a read-write attached terminal — the one state in which the
        /// composer's send is accepted.
        public async Task<FakeTerminalAttachClient> AttachAsync(string path) {
            await LoadAsync(path);
            var client = Attach.Created[^1];
            await client.TriggerAttached([]);
            Dispatcher.UIThread.RunJobs();
            return client;
        }

        /// Real key events into the focused composer: the TextBox's own key handling is exactly
        /// what these tests are about, so the text cannot be poked into the view model instead.
        public void Type(string text) {
            Composer.Focus();
            Dispatcher.UIThread.RunJobs();
            Window.KeyTextInput(text);
            Dispatcher.UIThread.RunJobs();
        }

        public void PressEnter(RawInputModifiers modifiers) {
            Window.KeyPressQwerty(PhysicalKey.Enter, modifiers);
            Dispatcher.UIThread.RunJobs();
        }

        public void Press(PhysicalKey key) {
            Window.KeyPressQwerty(key, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
        }

        /// Types, sends with Enter and lets the CR go out, so the next send is accepted.
        public async Task SendAsync(params string[] lines) {
            for (var i = 0; i < lines.Length; i++) {
                if (i > 0) PressEnter(RawInputModifiers.Shift);
                Type(lines[i]);
            }
            PressEnter(RawInputModifiers.None);
            Time.Advance(CrDelay);
            await Terminal.PendingDeliveryForTesting!;
        }

        public async Task LoadAsync(string path) {
            Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path });
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
            Settle();
        }

        public async Task AppendAndTickAsync(string path, int lines) {
            File.AppendAllLines(path, Enumerable.Repeat(UserLine, lines));
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task AppendLinesAndTickAsync(string path, params string[] lines) {
            File.AppendAllLines(path, lines);
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public bool AtBottom() => Scroll.Offset.Y + Scroll.Viewport.Height >= Scroll.Extent.Height - 1;

        public async Task CloseAsync() {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            await Chat.TeardownAsync();
            await Terminal.TeardownAsync();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_usage_limit_question_renders_its_choices_and_a_choice_sends_the_digit() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("limit.jsonl", [UserLine]);
            var client = await host.AttachAsync(path);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit · resets 3:10pm",
                "What do you want to do?", [
                    new(1, "Stop and wait for limit to reset"),
                    new(2, "Wait here, then continue automatically shortly"),
                    new(3, "Ask your admin for more usage"),
                ]);
            try {
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, Status = "Running", UsageLimit = notice,
                });
                await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
                host.Settle();

                var card = host.View.FindControl<Border>("TerminalMenuCard");
                await Assert.That(card!.IsVisible).IsTrue();
                await Assert.That(host.Composer.IsEnabled).IsFalse();
                var labels = host.View.GetVisualDescendants().OfType<TextBlock>()
                    .Select(t => t.Text)
                    .Where(t => t?.StartsWith("Stop", StringComparison.Ordinal) == true
                        || t?.StartsWith("Wait here", StringComparison.Ordinal) == true
                        || t?.StartsWith("Ask your admin", StringComparison.Ordinal) == true)
                    .ToArray();
                await Assert.That(labels).Count().IsEqualTo(3);

                await host.Chat.TerminalMenuChoices[2].Choose.Execute().ToTask();
                host.Settle();
                await Assert.That(client.SentInput.Any(bytes => bytes is [(byte)'3'])).IsTrue();
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ended_session_hides_stale_terminal_controls_and_shows_the_ended_hint() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("ended-dialog.jsonl", [UserLine]);
            await host.AttachAsync(path);
            var opened = false;
            host.Chat.ShowTerminal = () => opened = true;
            var agent = Agent("a1", "claude", hasTerminal: true) with {
                TranscriptPath = path, Status = "Running",
                TerminalDialog = new TerminalDialogDto("", "", [], -1, "Choose an MCP server"),
            };
            try {
                host.Daemon.Agents.AddOrUpdate(agent);
                host.Settle();
                var openTerminal = host.View.FindControl<Button>("TerminalMenuOpenTerminal")!;
                await Assert.That(openTerminal.IsEffectivelyVisible).IsTrue();
                Click(host, openTerminal);
                await Assert.That(opened).IsTrue();

                host.Daemon.Agents.AddOrUpdate(agent with { Status = "Completed" });
                host.Settle();

                await Assert.That(host.Composer.IsEffectivelyVisible).IsFalse();
                await Assert.That(host.View.FindControl<Border>("ComposerCard")!.IsEffectivelyVisible).IsFalse();
                await Assert.That(openTerminal.IsEffectivelyVisible).IsFalse();
                await Assert.That(openTerminal.Focus()).IsFalse();
                var hint = host.View.FindControl<TextBlock>("EndedComposerHint")!;
                await Assert.That(hint.IsEffectivelyVisible).IsTrue();
                await Assert.That(hint.Text).IsEqualTo("This session has ended");
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_account_strip_sits_under_the_composer_card_inside_the_band() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("account.jsonl", [UserLine]);
            try {
                var strip = host.View.FindControl<Border>("AccountStrip")!;
                var card = host.View.FindControl<Border>("ComposerCard")!;
                var band = host.View.FindControl<Border>("ComposerBand")!;
                await Assert.That(strip.IsVisible).IsFalse();

                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, AccountDirectory = "/home/me/.claude-work", AccountId = "acc1", AccountLabel = "work",
                });
                await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
                host.Settle();

                await Assert.That(strip.IsVisible).IsTrue();
                await Assert.That(strip.Parent).IsSameReferenceAs(card.Parent);
                await Assert.That(strip.GetVisualAncestors()).Contains(band);
                var cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), host.Window)!.Value.Y;
                await Assert.That(strip.TranslatePoint(default, host.Window)!.Value.Y).IsGreaterThanOrEqualTo(cardBottom);
                var texts = strip.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
                await Assert.That(texts).Contains("work");
                await Assert.That(texts).Contains("/home/me/.claude-work");
                await Assert.That(strip.GetVisualDescendants().OfType<ContentControl>().First().Content).IsNotNull();

                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path });
                host.Settle();
                await Assert.That(strip.IsVisible).IsFalse();
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_strip_draws_a_danger_pill_for_a_blocked_account() {
        await RunOnUiAsync(async () => {
            var readings = new BehaviorSubject<Capacitor.Cli.Core.Accounts.AccountsSnapshotDto?>(StripReadings.Snapshot(
                StripReadings.Account("acc1", StripReadings.Blocked(TimeSpan.FromMinutes(65),
                    StripReadings.Window(300, 100, TimeSpan.FromMinutes(65)), StripReadings.Window(10080, 81, TimeSpan.FromDays(3))))));
            var host = new Host(accountReadings: readings);
            var path = Tmp.CreateFile("blocked.jsonl", [UserLine]);
            try {
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, AccountDirectory = StripReadings.Directory, AccountId = "acc1", AccountLabel = "work",
                });
                await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
                await WaitUntilAsync(() => { host.Settle(); return host.Chat.ShowsAccountUsage; }, what: "the bars");

                var strip = host.View.FindControl<Border>("AccountStrip")!;
                var pill = host.View.FindControl<Border>("AccountStripBlocked")!;
                await Assert.That(pill.IsEffectivelyVisible).IsTrue();
                await Assert.That(pill.GetVisualDescendants().OfType<TextBlock>().Single().Text).IsEqualTo("Limit hit · resets in 1h 05m");
                await Assert.That(host.View.FindControl<TextBlock>("AccountStripDirectory")!.IsEffectivelyVisible).IsFalse();
                await Assert.That(host.View.FindControl<TextBlock>("AccountStripReset")!.IsEffectivelyVisible).IsFalse();
                var shown = strip.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                await Assert.That(shown).Contains("100%");
                await Assert.That(shown).Contains("81%");
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_strip_shows_an_exhausted_model_limit_beside_healthy_bars() {
        await RunOnUiAsync(async () => {
            var modelHit = new Capacitor.Cli.Core.Accounts.Readings.AccountReadingDto(1,
                [StripReadings.Window(300, 20, TimeSpan.FromHours(2)), new("claude_opus", 10080, 100, StripReadings.Now + TimeSpan.FromDays(2), StripReadings.Now)],
                null, null,
                new(StripReadings.Now.AddMinutes(-5), null, [new("claude_opus", 10080, StripReadings.Now + TimeSpan.FromDays(2), StripReadings.Now.AddMinutes(-5))]));
            var readings = new BehaviorSubject<Capacitor.Cli.Core.Accounts.AccountsSnapshotDto?>(
                StripReadings.Snapshot(StripReadings.Account("acc1", modelHit)));
            var host = new Host(accountReadings: readings);
            var path = Tmp.CreateFile("model-hit.jsonl", [UserLine]);
            try {
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, AccountDirectory = StripReadings.Directory, AccountId = "acc1", AccountLabel = "work",
                });
                await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
                await WaitUntilAsync(() => { host.Settle(); return host.Chat.ShowsAccountUsage; }, what: "the bars");

                var perModel = host.View.FindControl<TextBlock>("AccountStripPerModel")!;
                await Assert.That(perModel.IsEffectivelyVisible).IsTrue();
                await Assert.That(perModel.Text).IsEqualTo("+1 per model · 1 hit");
                await Assert.That(host.View.FindControl<Border>("AccountStripBlocked")!.IsEffectivelyVisible).IsFalse();
            } finally { await host.CloseAsync(); }
        });
    }

    /// Pins the two costs a long transcript could impose on the UI thread: one collection
    /// notification for the whole initial load, and containers for the viewport only.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_large_initial_load_is_one_notification_into_a_bounded_number_of_containers() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("big.jsonl", Enumerable.Repeat(UserLine, 5000).ToArray());
            var notifications = 0;
            ((INotifyCollectionChanged)host.Chat.Items).CollectionChanged += (_, _) => notifications++;

            await host.LoadAsync(path);

            await Assert.That(host.Chat.Items).Count().IsEqualTo(5000);
            await Assert.That(notifications).IsEqualTo(1);
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            await Assert.That(items.GetRealizedContainers().Count()).IsLessThan(200);
            await host.CloseAsync();
        });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [NotInParallel("AvaloniaSession")]
    public Task Scrolling_uneven_history_does_not_reverse_the_scrollbar(bool drag, bool down) => RunOnUiAsync(async () => {
        var host = new Host();
        try {
            var lines = UnevenHistory();
            var path = Tmp.CreateFile("uneven-scroll.jsonl", lines);
            await host.LoadAsync(path);
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            if (down) { WheelUp(host); host.Settle(); }
            var direction = down ? 1 : -1;
            var target = drag
                ? (Control)host.Scroll.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Name == "PART_VerticalScrollBar" && ReferenceEquals(bar.TemplatedParent, host.Scroll))
                    .GetVisualDescendants().OfType<Thumb>().Single()
                : items;
            var origin = PresentAndLocate(host, target);
            if (drag) { host.Window.MouseMove(origin); host.Window.MouseDown(origin, MouseButton.Left); }
            var previous = host.Scroll.Offset.Y / (host.Scroll.Extent.Height - host.Scroll.Viewport.Height);
            var reversals = new List<string>();
            var contentJumps = new List<string>();
            var peakRealized = 0;
            var allocated = GC.GetTotalAllocatedBytes();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 80; i++) {
                var middle = host.Scroll.Viewport.Height / 2;
                var reading = drag ? null : items.GetRealizedContainers()
                    .Select(container => (container, top: container.TranslatePoint(default, host.Scroll)?.Y))
                    .Where(row => row.top is { } top && top < host.Scroll.Viewport.Height && top + row.container.Bounds.Height > 0
                        && top <= middle && top + row.container.Bounds.Height > middle)
                    .OrderBy(row => Math.Max(0, Math.Max(row.top!.Value - middle, middle - row.top.Value - row.container.Bounds.Height)))
                    .Select(row => row.container)
                    .FirstOrDefault();
                var readingItem = reading?.DataContext;
                var beforeY = reading?.TranslatePoint(default, host.Scroll)?.Y;
                if (drag) host.Window.MouseMove(origin + new Vector(0, direction * (i + 1) * 2), RawInputModifiers.LeftMouseButton);
                else host.Window.MouseWheel(origin, new Vector(0, -direction));
                if (drag && i == 79) host.Window.MouseUp(origin + new Vector(0, direction * 160), MouseButton.Left);
                host.Settle();
                if (!drag) {
                    var after = readingItem is null ? null : items.ContainerFromItem(readingItem);
                    var shift = (after?.TranslatePoint(default, host.Scroll)?.Y - beforeY) * -direction;
                    if (!ReferenceEquals(after, reading) || shift is null or < -1 or > 75)
                        contentJumps.Add($"step {i}: reading row moved {shift:F1}px or was recycled");
                }
                var current = host.Scroll.Offset.Y / (host.Scroll.Extent.Height - host.Scroll.Viewport.Height);
                if ((current - previous) * direction < -0.005) reversals.Add($"step {i}: {previous:F4} -> {current:F4}");
                previous = current;
                peakRealized = Math.Max(peakRealized, items.GetRealizedContainers().Count());
            }
            watch.Stop();
            allocated = GC.GetTotalAllocatedBytes() - allocated;
            Console.WriteLine($"CHAT_SCROLL drag={drag} down={down} steps=80 elapsed_ms={watch.Elapsed.TotalMilliseconds:F2} allocated_bytes={allocated} peak_realized={peakRealized} reversals={reversals.Count} content_jumps={contentJumps.Count} {string.Join("; ", reversals.Concat(contentJumps))}");
            await Assert.That(reversals).IsEmpty();
            await Assert.That(contentJumps).IsEmpty();
            if (down) await Assert.That(previous).IsGreaterThan(0.01);
            else await Assert.That(previous).IsLessThan(0.99);
            await Assert.That(peakRealized).IsLessThan(80);
            await Assert.That(host.AtBottom()).IsFalse();
            if (!drag)
                for (var frame = 0; frame < 30; frame++) {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    host.Settle();
                }
            var offset = host.Scroll.Offset.Y;
            await host.AppendAndTickAsync(path, 5);
            await Assert.That(Math.Abs(host.Scroll.Offset.Y - offset)).IsLessThan(2);
        } finally { await host.CloseAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Resizing_uneven_history_preserves_the_readers_visible_row() => RunOnUiAsync(async () => {
        var host = new Host();
        try {
            var lines = UnevenHistory();
            var path = Tmp.CreateFile("resize-scroll.jsonl", lines);
            await host.LoadAsync(path);
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            var center = PresentAndLocate(host, items);
            for (var i = 0; i < 40; i++) {
                host.Window.MouseWheel(center, new Vector(0, 1));
                host.Settle();
            }
            // A frame's step scales with the wall-clock time since the last one, so back-to-back
            // ticks can leave the wheel still moving; the resize's frame would then scroll the reader.
            for (var frame = 0; host.View.IsSmoothScrolling; frame++) {
                if (frame == 500) throw new InvalidOperationException("The wheel scroll never settled.");
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                host.Settle();
            }
            var row = items.ItemsPanelRoot!.FindAncestorOfType<IScrollAnchorProvider>()!.CurrentAnchor
                ?? throw new InvalidOperationException("The scrolled chat has no anchor.");
            var item = row.DataContext!;
            var beforeY = row.TranslatePoint(default, host.Scroll)!.Value.Y;
            var heights = items.GetRealizedContainers().ToDictionary(container => container.DataContext!, container => container.Bounds.Height);
            host.Window.Width = 360;
            host.Settle();
            await Assert.That(heights.Any(entry => items.ContainerFromItem(entry.Key) is { } container && Math.Abs(container.Bounds.Height - entry.Value) > 20)).IsTrue();
            var ordered = items.Items.Cast<object>().Select(items.ContainerFromItem).OfType<Control>().ToArray();
            for (var i = 1; i < ordered.Length; i++)
                await Assert.That(Math.Abs(ordered[i].Bounds.Top - ordered[i - 1].Bounds.Bottom)).IsLessThan(0.5);
            await Assert.That(items.ContainerFromItem(item)).IsSameReferenceAs(row);
            await Assert.That(Math.Abs(row.TranslatePoint(default, host.Scroll)!.Value.Y - beforeY)).IsLessThan(2);
            await Assert.That(host.AtBottom()).IsFalse();
            var offset = host.Scroll.Offset.Y;
            await host.AppendAndTickAsync(path, 5);
            await Assert.That(Math.Abs(host.Scroll.Offset.Y - offset)).IsLessThan(2);
        } finally { await host.CloseAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Wheel_notches_interpolate_across_animation_frames() => RunOnUiAsync(async () => {
        var host = new Host();
        try {
            var path = Tmp.CreateFile("smooth-wheel.jsonl", Enumerable.Repeat(UserLine, 40).ToArray());
            await host.LoadAsync(path);
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            var markdown = host.View.GetVisualDescendants().OfType<MarkdownView>()
                .First(view => view.TranslatePoint(default, host.Scroll) is { } point
                    && point.Y >= 0 && point.Y < host.Scroll.Viewport.Height);
            var center = PresentAndLocate(host, markdown);
            var bottomRow = items.GetRealizedContainers().MaxBy(c => c.Bounds.Bottom)!;
            var startY = bottomRow.TranslatePoint(default, host.Scroll)!.Value.Y;
            Action<TimeSpan>? pending = null;
            host.View.RequestSmoothFrame = (_, frame) => pending = frame;

            host.Window.MouseWheel(center, new Vector(0, 1));
            host.Settle();
            var firstY = bottomRow.TranslatePoint(default, host.Scroll)!.Value.Y;
            await Assert.That(firstY - startY).IsGreaterThan(5).And.IsLessThan(45);

            var ys = new List<double> { firstY };
            for (var frame = 1; frame <= 30; frame++) {
                var next = pending;
                pending = null;
                next?.Invoke(TimeSpan.FromSeconds(frame / 60.0));
                host.Settle();
                ys.Add(bottomRow.TranslatePoint(default, host.Scroll)!.Value.Y);
            }
            for (var i = 1; i < ys.Count; i++)
                await Assert.That(ys[i]).IsGreaterThanOrEqualTo(ys[i - 1] - 0.01);
            await Assert.That(ys[^1] - startY).IsGreaterThan(49).And.IsLessThan(51);
            await Assert.That(ys.Distinct().Count()).IsGreaterThanOrEqualTo(4);
        } finally { await host.CloseAsync(); }
    });

    /// Pins follow-tail's whole contract: it tracks the bottom, leaves a reader who scrolled up
    /// where they are, and does not follow an append that lands in the same layout pass as the
    /// reader's scroll.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_tracks_the_bottom_and_leaves_a_scrolled_up_reader_alone() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("t.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendAndTickAsync(path, 20);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();

            WheelUp(host);
            host.Settle();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.AppendAndTickAsync(path, 20);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);

            // At the bottom, append, then wheel up before the layout pass runs: the one change
            // carries both, and the reader's gesture wins.
            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await Assert.That(host.AtBottom()).IsTrue();
            void ScrollUp(object? sender, NotifyCollectionChangedEventArgs e) => WheelUp(host);
            ((INotifyCollectionChanged)host.Chat.Items).CollectionChanged += ScrollUp;
            await host.AppendAndTickAsync(path, 20);
            ((INotifyCollectionChanged)host.Chat.Items).CollectionChanged -= ScrollUp;
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.CloseAsync();
        });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_resumes_after_a_selection_is_cleared_or_hidden(bool collapse, bool appendWhileSelected) {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var path = Tmp.CreateFile("selection.jsonl", Enumerable.Repeat(UserLine, 20).Append(ThinkingLine).ToArray());
                await host.LoadAsync(path);
                var markdown = host.View.GetVisualDescendants().OfType<MarkdownView>()
                    .Single(v => v.Classes.Contains("thinking"));
                PresentAndLocate(host, markdown);
                var block = markdown.GetVisualDescendants().OfType<TextBlock>()
                    .First(t => MarkdownViewHarness.Reads(t) == "weighing it");
                Point At(int index) {
                    var glyph = block.TextLayout.HitTestTextPosition(index);
                    return block.TranslatePoint(new Point(glyph.X + 1, glyph.Height / 2), host.Window)!.Value;
                }
                var start = At(0);
                var end = At(6);
                host.Window.MouseMove(start);
                host.Window.MouseDown(start, MouseButton.Left);
                host.Window.MouseMove(start + new Vector(0.1, 0), RawInputModifiers.LeftMouseButton);
                await Assert.That(markdown.HasSelection).IsFalse();
                await Assert.That(host.View.CanPreserveReadingPosition).IsFalse();
                host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                host.Window.MouseUp(end, MouseButton.Left);
                host.Settle();
                await Assert.That(markdown.HasSelection).IsTrue();
                await Assert.That(host.View.CanPreserveReadingPosition).IsTrue();
                await Assert.That(host.AtBottom()).IsTrue();

                if (appendWhileSelected) {
                    var offset = host.Scroll.Offset.Y;
                    await host.AppendAndTickAsync(path, 2);
                    host.Settle();
                    await Assert.That(host.Scroll.Offset.Y).IsEqualTo(offset);
                    await Assert.That(host.AtBottom()).IsFalse();
                    await Assert.That(markdown.HasSelection).IsTrue();
                }
                if (collapse) {
                    var summary = host.View.GetVisualDescendants().OfType<Button>()
                        .Single(b => b.Classes.Contains("thinkingSummary"));
                    Click(host, summary);
                    await Assert.That(markdown.HasSelection).IsTrue();
                    await Assert.That(markdown.IsEffectivelyVisible).IsFalse();
                    Action<TimeSpan>? pendingFrame = null;
                    host.View.RequestSmoothFrame = (_, frame) => pendingFrame = frame;
                    var timestamp = TimeSpan.FromSeconds(1);
                    void Wheel(double delta) {
                        host.Window.MouseWheel(PresentAndLocate(host, host.View.FindControl<ItemsControl>("ChatItems")!), new Vector(0, delta));
                        host.Settle();
                        for (var i = 0; i < 100 && pendingFrame is { } frame; i++) {
                            pendingFrame = null;
                            timestamp += TimeSpan.FromMilliseconds(16);
                            frame(timestamp);
                            host.Settle();
                        }
                    }
                    Wheel(100_000);
                    await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
                    Wheel(-100_000);
                    await Assert.That(host.AtBottom()).IsTrue();
                    await Assert.That(host.View.CanPreserveReadingPosition).IsFalse();
                } else {
                    PresentAndLocate(host, markdown);
                    var clear = At(9);
                    host.Window.MouseMove(clear);
                    host.Window.MouseDown(clear, MouseButton.Left);
                    host.Window.MouseUp(clear, MouseButton.Left);
                    host.Settle();
                    await Assert.That(markdown.HasSelection).IsFalse();
                    await Assert.That(host.View.CanPreserveReadingPosition).IsEqualTo(appendWhileSelected);
                    if (appendWhileSelected) {
                        host.Scroll.ScrollToEnd();
                        host.Settle();
                    }
                }
                await host.AppendAndTickAsync(path, 10);
                host.Settle();
                await Assert.That(host.AtBottom()).IsTrue();
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_code_padding_selection_holds_the_chat_while_output_arrives() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var code = ThinkingLine.Replace("weighing it", "```\\nhello world\\n```");
                var path = Tmp.CreateFile("padding-selection.jsonl", Enumerable.Repeat(UserLine, 20).Append(code).ToArray());
                await host.LoadAsync(path);
                var markdown = host.View.GetVisualDescendants().OfType<MarkdownView>().Single(v => v.Classes.Contains("thinking"));
                PresentAndLocate(host, markdown);
                var block = markdown.GetVisualDescendants().OfType<TextBlock>().Single(t => MarkdownViewHarness.Reads(t).Contains("hello world", StringComparison.Ordinal));
                var first = block.TextLayout.HitTestTextPosition(0);
                var last = block.TextLayout.HitTestTextPosition(8);
                var start = block.TranslatePoint(new Point(first.X + block.Padding.Left - 8, first.Height / 2 + block.Padding.Top), host.Window)!.Value;
                var end = block.TranslatePoint(new Point(last.X + block.Padding.Left, last.Height / 2 + block.Padding.Top), host.Window)!.Value;
                host.Window.MouseMove(start);
                host.Window.MouseDown(start, MouseButton.Left);
                host.Window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                host.Window.MouseUp(end, MouseButton.Left);
                host.Settle();
                await Assert.That(markdown.HasSelection).IsTrue();
                var offset = host.Scroll.Offset.Y;
                await host.AppendAndTickAsync(path, 2);
                host.Settle();
                await Assert.That(host.Scroll.Offset.Y).IsEqualTo(offset);
                await Assert.That(markdown.HasSelection).IsTrue();
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Selection_tracking_survives_clearing_and_reusing_a_markdown_row(bool plainFallback, bool reuse) {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var thinking = plainFallback
                    ? ThinkingLine.Replace("weighing it", string.Concat(Enumerable.Repeat("> ", 128)) + "x")
                    : ThinkingLine;
                var path = Tmp.CreateFile("tracked-selection.jsonl", Enumerable.Repeat(UserLine, 20).Append(thinking).ToArray());
                await host.LoadAsync(path);
                var markdown = host.View.GetVisualDescendants().OfType<MarkdownView>().Single(v => v.Classes.Contains("thinking"));
                var plain = markdown.Content as SelectableTextBlock;
                if (plainFallback) {
                    await Assert.That(plain).IsNotNull();
                    plain!.SelectionStart = 0;
                    plain.SelectionEnd = 4;
                } else markdown.SelectAll();
                await Assert.That(markdown.HasSelection).IsTrue();
                var offset = host.Scroll.Offset.Y;
                await host.AppendAndTickAsync(path, 2);
                host.Settle();
                await Assert.That(host.Scroll.Offset.Y).IsEqualTo(offset);
                await Assert.That(host.AtBottom()).IsFalse();
                if (reuse) markdown.Text = "replacement";
                else if (plain is not null) plain.ClearSelection();
                else markdown.ClearSelection();
                await Assert.That(markdown.HasSelection).IsFalse();
                host.Settle();
                host.Scroll.ScrollToEnd();
                host.Settle();
                await host.AppendAndTickAsync(path, 4);
                host.Settle();
                await Assert.That(host.AtBottom()).IsTrue();
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Sending_from_a_scrolled_up_chat_resumes_following(bool clickSend) {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var path = Tmp.CreateFile("send-scroll.jsonl", Enumerable.Repeat(UserLine, 40).ToArray());
                await host.AttachAsync(path);
                WheelUp(host);
                host.Settle();
                await Assert.That(host.AtBottom()).IsFalse();
                var markdown = host.View.GetVisualDescendants().OfType<MarkdownView>().First(v => v.IsEffectivelyVisible);
                markdown.SelectAll();
                await Assert.That(markdown.HasSelection).IsTrue();
                host.Type("continue");
                if (clickSend) Click(host, host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SendButton"));
                else host.PressEnter(RawInputModifiers.None);
                host.Time.Advance(CrDelay);
                await host.Terminal.PendingDeliveryForTesting!;
                host.Settle();
                await Assert.That(markdown.HasSelection).IsFalse();
                await Assert.That(host.AtBottom()).IsTrue();
                await host.AppendAndTickAsync(path, 10);
                host.Settle();
                await Assert.That(host.AtBottom()).IsTrue();
            } finally { await host.CloseAsync(); }
        });
    }

    /// The window reuses one view for every session it opens: a reader who scrolled up in one
    /// session must not open the next one scrolled up and off the tail.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_new_session_in_the_same_view_opens_at_the_bottom_and_follows() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                await host.LoadAsync(Tmp.CreateFile("first.jsonl", Enumerable.Repeat(UserLine, 60).ToArray()));
                WheelUp(host);
                host.Settle();
                await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);

                var next = new Host(show: false);
                try {
                    var nextPath = Tmp.CreateFile("second.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
                    await next.LoadAsync(nextPath);
                    host.View.DataContext = next.Chat;
                    host.Settle();
                    await Assert.That(host.AtBottom()).IsTrue();
                    var items = host.View.FindControl<ItemsControl>("ChatItems")!;
                    var last = items.ContainerFromItem(next.Chat.Items[^1])!;
                    var lastBottom = last.TranslatePoint(new Point(0, last.Bounds.Height), host.Scroll)!.Value.Y;
                    await Assert.That(Math.Abs(host.Scroll.Viewport.Height - lastBottom)).IsLessThan(40);

                    await next.AppendAndTickAsync(nextPath, 20);
                    host.Settle();
                    await Assert.That(host.AtBottom()).IsTrue();
                } finally { await next.CloseAsync(); }
            } finally { await host.CloseAsync(); }
        });
    }

    /// Pins that the initial load lands the reader at the bottom even when it completes before the
    /// view's first layout pass — an unshown window, and a tab collapsed from the start — so there
    /// is no ScrollViewer to read "was at end" from when the rows arrive.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_initial_load_before_the_first_layout_still_lands_the_reader_at_the_bottom() {
        await RunOnUiAsync(async () => {
            var unshown = new Host(show: false);
            await unshown.LoadAsync(Tmp.CreateFile("unshown.jsonl", Enumerable.Repeat(UserLine, 60).ToArray()));
            await Assert.That(unshown.Chat.Items).Count().IsEqualTo(60);
            await Assert.That(unshown.HasScroll).IsFalse();

            unshown.Show();

            await Assert.That(unshown.AtBottom()).IsTrue();
            await unshown.CloseAsync();

            var collapsed = new Host(show: false);
            collapsed.View.IsVisible = false;
            collapsed.Show();
            await collapsed.LoadAsync(Tmp.CreateFile("hidden.jsonl", Enumerable.Repeat(UserLine, 60).ToArray()));
            await Assert.That(collapsed.HasScroll).IsFalse();

            collapsed.View.IsVisible = true;
            collapsed.Settle();

            await Assert.That(collapsed.AtBottom()).IsTrue();
            await collapsed.CloseAsync();
        });
    }

    /// Pins that appends arriving while the surface is collapsed still leave the reader at the
    /// bottom once it is laid out again — the shape a Chat tab sitting behind the Terminal tab is
    /// in, where the view arms at most one pending scroll however many appends land.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Appends_while_the_surface_is_collapsed_still_land_the_reader_at_the_bottom() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("collapsed.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            host.View.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await host.AppendAndTickAsync(path, 20);
            await host.AppendAndTickAsync(path, 20);
            await Assert.That(host.Chat.Items).Count().IsEqualTo(100);

            host.View.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Pins the assistant template's one silent binding: a link rendered inside a chat row opens
    /// through the tab's own command, reached across the item boundary, and opens exactly once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_in_an_assistant_row_opens_through_the_tabs_command() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("link.jsonl", Enumerable.Repeat(UserLine, 40).Append(AssistantLinkLine).ToArray());
            await host.LoadAsync(path);

            await Assert.That(host.Chat.Items).Count().IsEqualTo(41);

            var paragraph = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("markdown-paragraph") && MarkdownViewHarness.Reads(t).Contains("docs", StringComparison.Ordinal));
            var glyph = paragraph.TextLayout.HitTestTextPosition("See ".Length);
            var origin = paragraph.TranslatePoint(new Point(glyph.X + paragraph.Padding.Left + 2, glyph.Y + paragraph.Padding.Top + glyph.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(origin);
            host.Window.MouseDown(origin, MouseButton.Left);
            await host.AppendAndTickAsync(path, 4);
            host.Settle();
            await Assert.That(host.AtBottom()).IsFalse();
            host.Window.MouseUp(origin, MouseButton.Left);
            host.Settle();

            await Assert.That(host.AtBottom()).IsTrue();
            await Assert.That(host.Opener.Opened).IsEquivalentTo(new[] { "https://example.com/docs" });
            await host.CloseAsync();
        });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [NotInParallel("AvaloniaSession")]
    public Task Follow_tail_resumes_after_a_primary_press(bool releaseOutside, bool loseCapture) => RunOnUiAsync(async () => {
        var host = new Host();
        try {
            var path = Tmp.CreateFile("press-tail.jsonl", Enumerable.Repeat(UserLine, 40).ToArray());
            await host.LoadAsync(path);
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            var point = PresentAndLocate(host, items);
            host.Window.MouseMove(point);
            host.Window.MouseDown(point, MouseButton.Left);
            await host.AppendAndTickAsync(path, 4);
            host.Settle();
            await Assert.That(host.AtBottom()).IsFalse();
            host.Window.MouseDown(point, MouseButton.Right, RawInputModifiers.LeftMouseButton);
            host.Window.MouseUp(point, MouseButton.Right, RawInputModifiers.LeftMouseButton);
            await host.AppendAndTickAsync(path, 2);
            host.Settle();
            await Assert.That(host.AtBottom()).IsFalse();
            if (loseCapture) {
                IPointer? pointer = null;
                host.Window.AddHandler(InputElement.PointerMovedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel);
                host.Window.MouseMove(point, RawInputModifiers.LeftMouseButton);
                pointer!.Capture(items);
                pointer.Capture(null);
            } else {
                var release = releaseOutside ? PresentAndLocate(host, host.Composer) : point;
                host.Window.MouseUp(release, MouseButton.Left);
            }
            host.Settle();
            await Assert.That(host.AtBottom()).IsTrue();
            if (loseCapture) host.Window.MouseUp(point, MouseButton.Left);
        } finally { await host.CloseAsync(); }
    });

    /// A labelled row shows its verb phrase beside the detail; once the result lands the same item
    /// renders as a card with the page's name and its actions, and the plain row is gone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_publish_row_turns_into_a_card_when_its_result_lands() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("pub.jsonl", [PublishCallLine]);
            await host.LoadAsync(path);

            var label = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToolRowLabel" && t.IsEffectivelyVisible);
            var detail = host.View.GetVisualDescendants().OfType<SelectableTextBlock>().Single(t => t.IsEffectivelyVisible && t.Text == "Retention brief");
            await Assert.That(label.Text).IsEqualTo("Published page");
            await Assert.That(label.FontFamily.Name).IsEqualTo(detail.FontFamily.Name);
            await Assert.That(label.LineHeight).IsEqualTo(detail.LineHeight);
            await Assert.That(label.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapTextBrush"));
            await Assert.That(detail.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapMutedBrush"));
            await Assert.That(host.View.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "ToolCard" && c.IsEffectivelyVisible)).IsFalse();

            await host.AppendLinesAndTickAsync(path, PublishResultLine);

            var card = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ToolCard" && b.IsEffectivelyVisible);
            await Assert.That(card.Margin.Left).IsEqualTo(0);
            await Assert.That(card.Margin.Right).IsEqualTo(0);
            var title = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToolCardTitle");
            var name = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToolCardName");
            var meta = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ToolCardMeta");
            await Assert.That(title.Text).IsEqualTo("Published page");
            await Assert.That(name.Text).IsEqualTo("Retention brief");
            await Assert.That(title.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapTextBrush"));
            await Assert.That(name.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapMutedBrush"));
            await Assert.That(meta.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapFaintBrush"));
            await Assert.That(name.TranslatePoint(default, host.Window)!.Value.X)
                .IsGreaterThan(title.TranslatePoint(default, host.Window)!.Value.X);
            await Assert.That(meta.TranslatePoint(default, host.Window)!.Value.X)
                .IsEqualTo(title.TranslatePoint(default, host.Window)!.Value.X);
            var openBtn = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolCardOpen");
            var copyBtn = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolCardCopyLink");
            var callsList = card.FindAncestorOfType<ItemsControl>()!;
            var header = host.View.GetVisualDescendants().OfType<ChatKindHeader>().Single(h => h.IsEffectivelyVisible);
            var headerIcon = header.GetVisualDescendants().OfType<AppIcon>().Single();
            await Assert.That(callsList.ClipToBounds).IsFalse();
            await Assert.That(headerIcon.Bounds.Center.Y).IsEqualTo(7.0).Within(0.5);
            await Assert.That(openBtn.IsEffectivelyVisible).IsTrue();
            await Assert.That(copyBtn.IsEffectivelyVisible).IsTrue();
            await Assert.That(openBtn.TranslatePoint(default, host.Window)!.Value.X)
                .IsGreaterThan(copyBtn.TranslatePoint(default, host.Window)!.Value.X);
            await Assert.That(ToolRows(host.View).Any(r => r.IsEffectivelyVisible)).IsFalse();
            await host.CloseAsync();
        });
    }

    /// Pins the tool row's outcome colour: the status pill takes the brush ToolOutcomeBrushConverter
    /// maps for the paired result, danger for an error and accent for a success.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_paired_tool_row_paints_its_status_dot_with_the_outcome_brush() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("tools.jsonl",
                [ToolCallLine, ToolResultLine, ToolCallLine.Replace("t1", "t2"), ToolErrorLine.Replace("t1", "t2")]));
            host.Settle();

            await Assert.That(OnlyGroup(host).Calls.Select(i => i.Outcome))
                .IsEquivalentTo([ToolOutcome.Done, ToolOutcome.Error], CollectionOrdering.Matching);
            var pills = ToolRows(host.View)
                .Select(row => row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toolStatus") && b.IsVisible))
                .ToList();

            await Assert.That(pills).Count().IsEqualTo(2);
            await Assert.That(pills[0].Background).IsSameReferenceAs(Brush(isError: false));
            await Assert.That(pills[1].Background).IsSameReferenceAs(Brush(isError: true));
            await host.CloseAsync();
        });
    }

    static object? Brush(bool isError) =>
        ToolOutcomeBrushConverter.Instance.Convert(isError, typeof(IBrush), null, CultureInfo.InvariantCulture);

    /// Pins that Enter reaches the send: the composer consumes it, the typed text leaves as one
    /// bracketed paste followed by the CR, and no newline is left behind in the box.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_sends_the_typed_text_and_leaves_no_newline_behind() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var client = await host.AttachAsync(Tmp.CreateFile("send.jsonl", UserLine));
            host.Type("hi");
            await Assert.That(host.Composer.Text).IsEqualTo("hi");

            host.PressEnter(RawInputModifiers.None);

            await Assert.That(host.Composer.Text ?? "").IsEqualTo("");
            await Assert.That(host.Chat.ComposerText).IsEqualTo("");
            await WaitUntilAsync(() => client.SentInput.Count == 1, what: "paste written");
            await Assert.That(client.SentInput[0]).IsEquivalentTo(TerminalInputEncoder.Paste("hi"));

            host.Time.Advance(CrDelay);
            await host.Terminal.PendingDeliveryForTesting!;
            await Assert.That(client.SentInput.Select(Encoding.UTF8.GetString))
                .IsEquivalentTo(["\x1b[200~hi\x1b[201~", "\r"], CollectionOrdering.Matching);
            await host.CloseAsync();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Interrupt_from_the_button_or_composer_preserves_the_draft_and_staged_files(bool button) {
        await RunOnUiAsync(async () => {
            var host = new Host(show: false);
            host.Window.Width = 550;
            host.Show();
            try {
                var path = Tmp.CreateFile("escape.jsonl", [UserLine]);
                var client = await host.AttachAsync(path);
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, Status = "Running", AwaitingInput = false, TurnAttested = true,
                });
                host.Type("keep this draft");
                host.Chat.Tray.AddAll([new StagedAttachment("notes.txt", "text/plain", new byte[] { 1 })]);
                if (button) Click(host, host.View.FindControl<Button>("InterruptButton")!);
                else host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                host.Settle();
                await Assert.That(client.SentInput).Count().IsEqualTo(1);
                await Assert.That(client.SentInput[0]).IsEquivalentTo(new byte[] { 0x1b });
                await Assert.That(host.Composer.Text).IsEqualTo("keep this draft");
                await Assert.That(host.Chat.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(["notes.txt"]);
                await Assert.That(host.Chat.ShowsComposer).IsTrue();
            } finally { await host.CloseAsync(); }
        });
    }

    /// A Working status is not proof of a turn: an idle PTY prompt reports AwaitingInput false
    /// until a Stop lands. The button stays, so the shortcut's own gate is what keeps Escape off it.
    [Test]
    [Arguments(false, null, 0)]
    [Arguments(false, false, 0)]
    [Arguments(true, false, 1)]
    [NotInParallel("AvaloniaSession")]
    public async Task Escape_does_not_reach_a_prompt_without_a_confirmed_foreground_turn(
            bool? awaitingInput, bool? turnAttested, int liveSubagents) {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var path = Tmp.CreateFile("escape-idle.jsonl", [UserLine]);
                var client = await host.AttachAsync(path);
                var ancestorSawEscape = false;
                host.Window.KeyDown += (_, e) => ancestorSawEscape |= e.Key == Key.Escape;
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, Status = "Running", AwaitingInput = awaitingInput, TurnAttested = turnAttested,
                    LiveSubagents = liveSubagents,
                });
                host.Settle();
                await Assert.That(host.Chat.ShowsInterrupt).IsTrue();
                await Assert.That(host.Chat.CanInterruptWithEscape).IsFalse();
                await Assert.That(host.View.FindControl<Button>("InterruptButton")!.Content).IsEqualTo("Interrupt");
                host.Type("keep this draft");
                host.Press(PhysicalKey.Escape);
                await Assert.That(client.SentInput).IsEmpty();
                await Assert.That(ancestorSawEscape).IsFalse();
                await Assert.That(host.Composer.Text).IsEqualTo("keep this draft");
                // An attested turn may be interrupted; the same shortcut goes inert as soon as it ends.
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, Status = "Running", AwaitingInput = false, TurnAttested = true,
                    LiveSubagents = liveSubagents,
                });
                host.Settle();
                await Assert.That(host.View.FindControl<Button>("InterruptButton")!.Content).IsEqualTo("Interrupt (Esc)");
                host.Press(PhysicalKey.Escape);
                await Assert.That(client.SentInput).Count().IsEqualTo(1);
                await Assert.That(client.SentInput[0]).IsEquivalentTo(new byte[] { 0x1b });
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, Status = "Running", AwaitingInput = true, TurnAttested = false,
                    LiveSubagents = liveSubagents,
                });
                host.Settle();
                host.Press(PhysicalKey.Escape);
                await Assert.That(client.SentInput).Count().IsEqualTo(1);
                await Assert.That(host.Terminal.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
                await Assert.That(host.Composer.Text).IsEqualTo("keep this draft");
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_child_control_can_consume_Escape_before_the_chat_interrupts() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var path = Tmp.CreateFile("escape-child.jsonl", [UserLine]);
                var client = await host.AttachAsync(path);
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, Status = "Running", AwaitingInput = false, TurnAttested = true,
                });
                host.Settle();
                await Assert.That(host.Chat.CanInterruptWithEscape).IsTrue();
                host.Type("keep this draft");
                var consumed = false;
                host.Composer.KeyDown += (_, e) => {
                    if (e.Key != Key.Escape) return;
                    consumed = true;
                    e.Handled = true;
                };
                host.Press(PhysicalKey.Escape);
                await Assert.That(consumed).IsTrue();
                await Assert.That(client.SentInput).IsEmpty();
                await Assert.That(host.Composer.Text).IsEqualTo("keep this draft");
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Escape_dismisses_the_composer_context_menu_before_interrupting_the_turn() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var path = Tmp.CreateFile("escape-menu.jsonl", [UserLine]);
                var client = await host.AttachAsync(path);
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path, Status = "Running", AwaitingInput = false, TurnAttested = true,
                });
                host.Settle();
                await Assert.That(host.Chat.CanInterruptWithEscape).IsTrue();
                host.Type("keep this draft");
                var item = new MenuItem { Header = "Copy" };
                var menu = new ContextMenu { ItemsSource = new[] { item } };
                host.Composer.ContextMenu = menu;
                menu.Open(host.Composer);
                host.Settle();
                item.Focus();
                await Assert.That(menu.IsOpen).IsTrue();
                host.Press(PhysicalKey.Escape);
                await Assert.That(menu.IsOpen).IsFalse();
                await Assert.That(client.SentInput).IsEmpty();
                host.Composer.Focus();
                host.Press(PhysicalKey.Escape);
                await Assert.That(client.SentInput).Count().IsEqualTo(1);
                await Assert.That(client.SentInput[0]).IsEquivalentTo(new byte[] { 0x1b });
                await Assert.That(host.Composer.Text).IsEqualTo("keep this draft");
            } finally { await host.CloseAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_queue_banner_shows_sent_text_until_the_transcript_echoes_it() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("queue.jsonl", [UserLine]);
            var client = await host.AttachAsync(path);
            host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path, Status = "Running", AwaitingInput = true });
            host.Type("queued follow-up");
            host.PressEnter(RawInputModifiers.None);
            host.Settle();
            var banner = host.View.FindControl<Border>("QueuedMessagesBanner")!;
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "queued follow-up")).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Delivery unconfirmed" && t.IsVisible)).IsFalse();
            // The paste is still in flight, so Send appears once the channel will take text.
            host.Time.Advance(CrDelay);
            await host.Terminal.PendingDeliveryForTesting!;
            host.Settle();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == QueuedChatMessage.IdlePickupNote && t.IsVisible)).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == QueuedChatMessage.AwaitingPickupNote && t.IsVisible)).IsFalse();
            var queued = host.Chat.QueuedMessages.Single();
            var send = banner.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SendQueuedButton" && b.IsVisible);
            await Assert.That(send.Content).IsEqualTo("Send again");
            host.Type("queued follow-up");
            Click(host, send);
            await WaitUntilAsync(() => client.SentInput.Count == 3, what: "queued text pasted a second time");
            await Assert.That(client.SentInput[2]).IsEquivalentTo(TerminalInputEncoder.Paste("queued follow-up"));
            await Assert.That(host.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(queued);
            await Assert.That(host.Composer.Text).IsEqualTo("queued follow-up");
            host.Time.Advance(CrDelay);
            await host.Terminal.PendingDeliveryForTesting!;
            await Assert.That(client.SentInput[3]).IsEquivalentTo(new byte[] { 13 });
            host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path, Status = "Running", AwaitingInput = false });
            host.Settle();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == QueuedChatMessage.AwaitingPickupNote && t.IsVisible)).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "SendQueuedButton" && b.IsVisible)).IsFalse();
            host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path, Status = "Completed" });
            host.Settle();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Delivery unconfirmed" && t.IsVisible)).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == QueuedChatMessage.AwaitingPickupNote && t.IsVisible)).IsFalse();
            await host.AppendLinesAndTickAsync(path, UserLine.Replace("hello", "queued follow-up"));
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(host.Composer.Text).IsEqualTo("queued follow-up");
            await host.CloseAsync();
        });
    }

    /// Pins the arrow keys' contract: ↑ in an empty composer recalls the last sent prompt, ↑ inside
    /// a multi-line recall climbs to the first line before stepping older, ↓ past the newest
    /// restores the empty box, and neither key touches a draft the user typed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Up_recalls_sent_prompts_and_down_returns_to_the_empty_draft() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.AttachAsync(Tmp.CreateFile("recall.jsonl", UserLine));
            await host.SendAsync("first");
            await host.SendAsync("top", "bottom");
            var multi = "top" + Environment.NewLine + "bottom";
            await Assert.That(host.Composer.Text ?? "").IsEqualTo("");

            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo(multi);
            await Assert.That(host.Composer.CaretIndex).IsEqualTo(multi.Length);

            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo(multi);
            await Assert.That(host.Composer.CaretIndex).IsLessThanOrEqualTo(3);

            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo("first");
            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo("first");

            host.Press(PhysicalKey.ArrowDown);
            await Assert.That(host.Composer.Text).IsEqualTo(multi);
            host.Press(PhysicalKey.ArrowDown);
            await Assert.That(host.Composer.Text ?? "").IsEqualTo("");

            host.Type("typing");
            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo("typing");
            await host.CloseAsync();
        });
    }

    /// A selection makes ↑/↓ the TextBox's own collapse-and-move, so a recall must not replace
    /// the text underneath it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Arrow_keys_over_a_selection_leave_the_recalled_text_alone() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.AttachAsync(Tmp.CreateFile("recall-selection.jsonl", UserLine));
            await host.SendAsync("first");
            await host.SendAsync("second");

            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo("second");
            host.Composer.SelectionStart = 0;
            host.Composer.SelectionEnd = 3;
            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo("second");

            host.Composer.SelectionStart = 6;
            host.Composer.SelectionEnd = 2;
            host.Press(PhysicalKey.ArrowDown);
            await Assert.That(host.Composer.Text).IsEqualTo("second");
            await host.CloseAsync();
        });
    }

    /// The composer wraps, so a long prompt with no newline still has rows above the caret: ↑ from
    /// its end climbs those rows first and recalls the older prompt only from the top one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Up_climbs_the_wrapped_rows_of_a_recall_before_stepping_older() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.AttachAsync(Tmp.CreateFile("recall-wrap.jsonl", UserLine));
            await host.SendAsync("first");
            var wrapped = string.Join(' ', Enumerable.Repeat("wrapped", 60));
            await host.SendAsync(wrapped);

            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo(wrapped);
            await Assert.That(host.Composer.GetLineCount()).IsGreaterThan(1);

            host.Press(PhysicalKey.ArrowUp);
            await Assert.That(host.Composer.Text).IsEqualTo(wrapped);
            await Assert.That(host.Composer.CaretIndex).IsLessThan(wrapped.Length);

            var presses = 1;
            while (host.Composer.Text == wrapped && presses < 20) {
                host.Press(PhysicalKey.ArrowUp);
                presses++;
            }
            await Assert.That(host.Composer.Text).IsEqualTo("first");
            await host.CloseAsync();
        });
    }

    /// Pins the other half of the key contract: Shift+Enter stays the TextBox's own newline and
    /// sends nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Shift_enter_inserts_a_newline_and_sends_nothing() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var client = await host.AttachAsync(Tmp.CreateFile("newline.jsonl", UserLine));
            host.Type("hi");

            host.PressEnter(RawInputModifiers.Shift);

            await Assert.That(host.Composer.Text).IsEqualTo("hi" + Environment.NewLine);
            await Assert.That(client.SentInput).IsEmpty();
            await host.CloseAsync();
        });
    }

    /// Pins that a refused send still consumes Enter: nothing goes out and the text survives
    /// intact, so the user can send it again once the hint's reason clears.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_on_a_refused_send_neither_sends_nor_inserts_a_newline() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Type("hi");
            await Assert.That(host.Terminal.CanAcceptText).IsFalse();

            host.PressEnter(RawInputModifiers.None);

            await Assert.That(host.Composer.Text).IsEqualTo("hi");
            await Assert.That(host.Chat.ComposerText).IsEqualTo("hi");
            await Assert.That(host.Attach.Created).IsEmpty();
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_user_turn_renders_as_markdown() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("user.jsonl",
                ["""{"type":"user","message":{"role":"user","content":"say **hi** there"}}"""]));
            host.Settle();
            var view = host.View.GetVisualDescendants().OfType<MarkdownView>().Single();
            var paragraph = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("markdown-paragraph"));
            await Assert.That(paragraph.Inlines!.OfType<Bold>().Count()).IsEqualTo(1);
            await Assert.That(paragraph.Inlines!.Text).IsEqualTo("say hi there");
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_bang_command_renders_as_a_command_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("bash.jsonl", [
                """{"type":"user","message":{"content":"<bash-input>kubectl get pods</bash-input>"}}""",
                """{"type":"user","message":{"content":"<bash-stdout>NAME\nweb</bash-stdout><bash-stderr>warn</bash-stderr>"}}""",
            ]));
            host.Settle();
            var command = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("shellCommand"));
            await Assert.That(command.HorizontalAlignment).IsEqualTo(Avalonia.Layout.HorizontalAlignment.Right);
            var chip = command.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("toolKindChip"));
            await Assert.That(chip.Text).IsEqualTo("You");
            var commandLine = command.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            await Assert.That(commandLine.Text).IsEqualTo("! kubectl get pods");
            await Assert.That(commandLine.Classes).Contains("toolLine");
            var output = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("shellOutput"));
            await Assert.That(output.HorizontalAlignment).IsEqualTo(Avalonia.Layout.HorizontalAlignment.Left);
            var outputChip = output.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("toolKindChip"));
            await Assert.That(outputChip.Text).IsEqualTo("Output");
            var outputLine = output.GetVisualDescendants().OfType<SelectableTextBlock>().Single();
            await Assert.That(outputLine.Text).IsEqualTo("NAME\nweb\nwarn");
            await Assert.That(outputLine.Classes).Contains("prose");
            await Assert.That(command.GetVisualDescendants().OfType<MarkdownView>().Any()).IsFalse();
            await Assert.That(output.GetVisualDescendants().OfType<MarkdownView>().Any()).IsFalse();
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Chat_bubbles_carry_a_kind_chip() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("chips.jsonl", [
                """{"type":"user","message":{"role":"user","content":"hello"}}""",
                """{"type":"assistant","message":{"content":[{"type":"text","text":"hi"}]}}""",
                """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""",
            ]));
            host.Settle();
            var chips = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("toolKindChip") && t.IsEffectivelyVisible)
                .Select(t => t.Text)
                .ToList();
            await Assert.That(chips).Contains("You");
            await Assert.That(chips).Contains(host.Chat.AssistantTitle);
            await Assert.That(chips).Contains("Note");
            var you = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("toolKindChip") && t.Text == "You");
            await Assert.That(you.HorizontalAlignment).IsEqualTo(Avalonia.Layout.HorizontalAlignment.Left);

            host.Chat.Items.OfType<UserTurnItem>().Single().Sender.Title = "Tony";
            host.Settle();
            await Assert.That(you.Text).IsEqualTo("Tony");
            await host.CloseAsync();
        });
    }

    /// Pins the fold: settled calls become one summary line, live calls stay as rows, and a click
    /// on the summary reveals every call and hides them again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_folds_settled_calls_into_a_summary_and_expands_on_click() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("fold.jsonl", [ToolCallLine, ToolResultLine, ReadCallLine, ReadResultLine, CallLine(3)]));

            await Assert.That(host.Chat.Items).Count().IsEqualTo(1);
            var summary = Summary(host.View);
            await Assert.That(summary.IsVisible).IsTrue();
            await Assert.That(OnlyGroup(host).Summary).IsEqualTo("Searched files, read a file");
            await Assert.That(summary.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))
                .Contains("Searched files, read a file");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await Assert.That(((ToolCallItem)ToolRows(host.View)[0].DataContext!).Outcome).IsEqualTo(ToolOutcome.Running);
            var card = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toolGroup"));
            await Assert.That(card.Classes.Contains("folded")).IsTrue();
            await Assert.That(card.Margin.Bottom).IsEqualTo(10);

            Click(host, summary);
            await Assert.That(OnlyGroup(host).IsExpanded).IsTrue();
            await Assert.That(OnlyGroup(host).Summary).IsEqualTo("Searched files, read a file");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(3);
            await Assert.That(card.Classes.Contains("folded")).IsFalse();
            await Assert.That(card.Margin.Bottom).IsEqualTo(22);

            Click(host, summary);
            await Assert.That(OnlyGroup(host).IsExpanded).IsFalse();
            await Assert.That(OnlyGroup(host).Summary).IsEqualTo("Searched files, read a file");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await host.CloseAsync();
        });
    }

    /// A lone settled call is the row itself — no "Ran a command" summary, but a kind chip names it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_single_settled_call_shows_the_row_without_a_summary() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("one.jsonl", [ToolCallLine, ToolResultLine]));
            await Assert.That(OnlyGroup(host).ShowsSummaryHeader).IsFalse();
            await Assert.That(OnlyGroup(host).ShowsKindChip).IsTrue();
            await Assert.That(OnlyGroup(host).KindChip).IsEqualTo("Search");
            await Assert.That(SummaryOrNull(host.View)?.IsVisible ?? false).IsFalse();
            host.Settle();
            var header = host.View.GetVisualDescendants().OfType<ChatKindHeader>()
                .Single(h => h.IsEffectivelyVisible);
            await Assert.That(header.Label).IsEqualTo("Search");
            await Assert.That(header.IconKind).IsNotNull();
            var chip = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("toolKindChip") && t.IsEffectivelyVisible);
            await Assert.That(chip.Text).IsEqualTo("Search");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await Assert.That(((ToolCallItem)ToolRows(host.View)[0].DataContext!).Outcome).IsEqualTo(ToolOutcome.Done);
            await host.CloseAsync();
        });
    }

    /// A group with nothing settled has no summary line at all.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_of_only_live_calls_shows_rows_and_no_summary() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("live.jsonl", [ToolCallLine, ReadCallLine]));
            await Assert.That(SummaryOrNull(host.View)?.IsVisible ?? false).IsFalse();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(2);
            await host.CloseAsync();
        });
    }

    /// A failed group opens so the row error pills are in view; the summary header has none.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_call_inside_a_multi_call_group_opens_the_group() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("fail.jsonl", [
                ToolCallLine, ToolErrorLine, ReadCallLine, ReadResultLine,
            ]));
            var group = OnlyGroup(host);
            await Assert.That(group.HasFailure).IsTrue();
            await Assert.That(group.IsExpanded).IsTrue();
            var summary = Summary(host.View);
            await Assert.That(summary.IsVisible).IsTrue();
            await Assert.That(summary.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("toolStatus") && b.IsVisible)).IsEqualTo(0);
            var pills = ToolRows(host.View)
                .Select(row => row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toolStatus") && b.IsVisible))
                .ToList();
            await Assert.That(pills).Count().IsEqualTo(2);
            await Assert.That(pills[0].Background).IsSameReferenceAs(Brush(isError: true));
            await Assert.That(pills[1].Background).IsSameReferenceAs(Brush(isError: false));
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_awaiting_row_paints_the_question_glyph_with_the_accent_brush() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("ask.jsonl", [ToolCallLine]));
            host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => OnlyGroup(host).Calls[0].IsAwaitingPermission, what: "the mark");
            host.Settle();
            var call = OnlyGroup(host).Calls[0];
            await Assert.That(call.IsRunning).IsFalse();
            var glyph = host.View.GetVisualDescendants().OfType<AppIcon>()
                .Single(icon => icon.DataContext is ToolCallItem &&
                                icon.Kind == Lucide.Avalonia.LucideIconKind.CircleQuestionMark && icon.IsEffectivelyVisible);
            await Assert.That(glyph.Foreground).IsSameReferenceAs(Brush(isError: false));
            await Assert.That(host.View.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsEqualTo(0);
            // A pill that is not shown must not be on the clock either: an idle marker that still
            // ticks keeps the timer, and the window's render pass, alive for nothing.
            await Assert.That(host.View.GetVisualDescendants().OfType<Visual>().Any(PulseClock.GetIsActive)).IsFalse();
            await host.CloseAsync();
        });
    }

    /// Reasoning renders as collapsible muted Markdown under a Thinking header and left rule.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thinking_row_renders_its_text_under_a_thinking_chip() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            const string line = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Checking `LaunchMarkers.HasEntry` next.\nSecond line."}]}}""";
            await host.LoadAsync(Tmp.CreateFile("think.jsonl", [line]));
            var item = (AssistantThinkingItem)host.Chat.Items.Single();
            await Assert.That(item.IsExpanded).IsTrue();
            await Assert.That(item.HeaderLabel).IsEqualTo("Thinking");

            var bodyBorder = host.View.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("thinkingBody"));
            await Assert.That(bodyBorder.IsEffectivelyVisible).IsTrue();
            var paragraph = bodyBorder.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Classes.Contains("markdown-paragraph") && t.IsEffectivelyVisible);
            await Assert.That(paragraph.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapMutedBrush"));
            await Assert.That(paragraph.FontSize).IsEqualTo(13.5);

            item.Toggle();
            host.Settle();
            await Assert.That(item.IsExpanded).IsFalse();
            await Assert.That(item.HeaderLabel).IsEqualTo("Thinking · Checking LaunchMarkers.HasEntry next.");
            await Assert.That(bodyBorder.IsEffectivelyVisible).IsFalse();
            await host.CloseAsync();
        });
    }

    /// A bang fence in reasoning offers copy and no Run it. The same fence in the reply still does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thinking_bang_fence_has_no_run_button() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            const string thinking = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"```bash\n! kcap agent ls\n```"}]}}""";
            const string reply = """{"type":"assistant","message":{"content":[{"type":"text","text":"```bash\n! kcap agent ls\n```"}]}}""";
            await host.LoadAsync(Tmp.CreateFile("think-run.jsonl", [thinking, reply]));

            var body = host.View.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("thinkingBody"));
            await Assert.That(body.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("markdown-code-copy"))).IsEqualTo(1);
            await Assert.That(body.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("markdown-code-run"))).IsFalse();
            await Assert.That(host.View.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("markdown-code-run"))).IsEqualTo(1);
            await host.CloseAsync();
        });
    }

    /// A live row that is not waiting on permission shows the pulsing status pill.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_running_row_shows_the_pulsing_status_dot() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("run.jsonl", [ToolCallLine]));
            var call = OnlyGroup(host).Calls[0];
            await Assert.That(call.IsRunning).IsTrue();
            await Assert.That(call.HasDetail).IsTrue();
            var pulse = ToolRows(host.View)[0].GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("toolRunning") && b.IsVisible);
            await Assert.That(pulse.Background).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapWarningBrush"));
            await Assert.That(PulseClock.GetIsActive(pulse)).IsTrue();
            var rowLabel = ToolRows(host.View)[0].GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Name == "ToolRowLabel" && t.IsEffectivelyVisible);
            var detail = ToolRows(host.View)[0].GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.IsEffectivelyVisible && t.Text == "ls -la");
            await Assert.That(rowLabel.Text).IsEqualTo("Searched files");
            await Assert.That(rowLabel.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapTextBrush"));
            await Assert.That(detail.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapMutedBrush"));
            await host.CloseAsync();
        });
    }

    /// The inner lists mutate without an outer collection notification; follow-tail still reads
    /// the extent change.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_tracks_inner_group_growth_and_folding_and_leaves_a_scrolled_up_reader_alone() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("inner.jsonl", [.. Enumerable.Repeat(UserLine, 60), CallLine(1)]);
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendLinesAndTickAsync(path, CallLine(2), CallLine(3));
            await Assert.That(host.Chat.Items).Count().IsEqualTo(61);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendLinesAndTickAsync(path, ResultLine(1));
            await Assert.That(host.AtBottom()).IsTrue();

            WheelUp(host);
            host.Settle();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.AppendLinesAndTickAsync(path, CallLine(4), ResultLine(2));
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.CloseAsync();
        });
    }

    /// Expanding keeps the clicked header in place: the click is the reader's gesture and it
    /// lands above the bottom, so following stops until the reader returns to the bottom, when
    /// the next append follows again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Expanding_the_trailing_group_keeps_the_offset_and_returning_to_the_bottom_resumes_following() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>(Enumerable.Repeat(UserLine, 60));
            for (var i = 1; i <= 30; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            var path = Tmp.CreateFile("expand.jsonl", lines.ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();
            var summary = Summary(host.View);
            var beforeY = SummaryViewportY(host, summary);

            Click(host, summary);
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(((ToolGroupItem)host.Chat.Items[^1]).IsExpanded).IsTrue();
            summary = Summary(host.View);
            await Assert.That(Math.Abs(SummaryViewportY(host, summary) - beforeY)).IsLessThan(2);
            await Assert.That(host.AtBottom()).IsFalse();

            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 5);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    static double SummaryViewportY(Host host, Button summary) =>
        summary.TranslatePoint(new Point(0, 0), host.Scroll)?.Y
        ?? throw new InvalidOperationException("the summary is not in the scroll viewer");

    /// A row that changes height makes the virtualizing panel drop its anchor and re-place every
    /// row from the average size, which jumps the reader off the bubble they just toggled.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Expanding_and_collapsing_a_group_keeps_its_header_where_it_was() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>(Enumerable.Repeat(UserLine, 40));
            for (var i = 1; i <= 30; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            lines.AddRange(Enumerable.Repeat(UserLine, 40));
            await host.LoadAsync(Tmp.CreateFile("mid.jsonl", lines.ToArray()));

            WheelUp(host);
            host.Settle();
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            var group = host.Chat.Items.OfType<ToolGroupItem>().Single();
            items.ScrollIntoView(group);
            host.Settle();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            var summary = Summary(host.View);
            var before = SummaryViewportY(host, summary);
            await Assert.That(before).IsGreaterThan(0);
            await Assert.That(before).IsLessThan(host.Scroll.Viewport.Height);

            Click(host, summary);
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(group.IsExpanded).IsTrue();
            summary = Summary(host.View);
            await Assert.That(Math.Abs(SummaryViewportY(host, summary) - before)).IsLessThan(2);

            Click(host, summary);
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(group.IsExpanded).IsFalse();
            summary = Summary(host.View);
            await Assert.That(Math.Abs(SummaryViewportY(host, summary) - before)).IsLessThan(2);
            await host.CloseAsync();
        });
    }

    /// An append that lands in the same dispatcher turn as the click shares its layout pass, so it
    /// is the reader's change too: the reader stays put, and only a later append, after they return
    /// to the bottom, follows.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_append_queued_behind_the_expansion_click_does_not_move_the_reader() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>(Enumerable.Repeat(UserLine, 60));
            for (var i = 1; i <= 30; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            var path = Tmp.CreateFile("race.jsonl", lines.ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();
            var summary = Summary(host.View);
            var beforeY = SummaryViewportY(host, summary);

            var origin = PresentAndLocate(host, summary);
            host.Window.MouseDown(origin, MouseButton.Left);
            host.Window.MouseUp(origin, MouseButton.Left);
            File.AppendAllLines(path, Enumerable.Repeat(UserLine, 5));
            host.Time.Advance(ChatTabViewModel.PollInterval);
            await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
            host.Settle();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            await Assert.That(((ToolGroupItem)host.Chat.Items[^6]).IsExpanded).IsTrue();
            await Assert.That(host.Chat.Items).Count().IsEqualTo(66);
            summary = Summary(host.View);
            await Assert.That(Math.Abs(SummaryViewportY(host, summary) - beforeY)).IsLessThan(2);

            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 5);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Expansion realizes every row; folding releases them again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thousand_call_group_realizes_on_expansion_and_releases_on_fold() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>();
            for (var i = 1; i <= 1000; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            lines.Add(CallLine(1001));
            await host.LoadAsync(Tmp.CreateFile("thousand.jsonl", lines.ToArray()));
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;

            await Assert.That(host.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(items.GetRealizedContainers().Count()).IsEqualTo(1);
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);

            OnlyGroup(host).Toggle();
            host.Settle();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1001);
            await Assert.That(items.GetRealizedContainers().Count()).IsEqualTo(1);

            OnlyGroup(host).Toggle();
            host.Settle();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await host.CloseAsync();
        });
    }

    /// Pins follow-tail against virtualization's estimate: an appended row far taller than the
    /// rows around it still leaves the reader at the real bottom once it is measured.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_lands_at_the_bottom_when_the_appended_row_is_taller_than_the_estimate() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("tall.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            var reply = string.Join("\\n\\n", Enumerable.Range(1, 25).Select(i => $"Paragraph {i} of a long reply."));
            File.AppendAllLines(path, ["{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + reply + "\"}]}}"]);
            host.Time.Advance(ChatTabViewModel.PollInterval);
            await (host.Chat.PendingReadForTesting ?? Task.CompletedTask);
            host.Settle();

            await Assert.That(host.Chat.Items.Count).IsEqualTo(61);
            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Pins the system note's surface: a muted card carrying the note as markdown, distinct from
    /// the user bubble and the assistant prose around it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_system_note_renders_as_a_muted_markdown_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("note.jsonl", [
                """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll **good**.\n</result>\n</task-notification>"}}""",
            ]));

            var card = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("systemNote"));
            var text = card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("markdown-paragraph")).ToList();
            await Assert.That(text.Select(t => t.Inlines?.Text ?? t.Text ?? "")).IsEquivalentTo(new[] { "Agent finished", "All good." }, CollectionOrdering.Matching);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Card_renders_with_its_buttons_and_leaves_the_list_when_empty() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await Assert.That(host.View.FindControl<Border>("NeedsYouRow")).IsNull();
            await Assert.That(host.Chat.Items.OfType<PendingCardItem>().Any()).IsFalse();

            host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Bash"));
            await WaitUntilAsync(() => host.Chat.Items.OfType<PendingCardItem>().Any(), what: "the card");
            Dispatcher.UIThread.RunJobs();
            var list = host.View.FindControl<ItemsControl>("ChatItems")!;
            var buttons = list.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString() ?? "").ToArray();
            await Assert.That(buttons).IsEquivalentTo(new[] { "Deny", "Allow always", "Allow" });

            host.Permissions.Remove("r1");
            await WaitUntilAsync(() => !host.Chat.Items.OfType<PendingCardItem>().Any(), what: "cleared");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(list.GetVisualDescendants().OfType<Button>().Any(b => b.Content is "Allow")).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Question_card_renders_options_other_and_coexists_with_a_permission_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Permissions.Add(PermissionEntries.Entry("r1"));
            host.Permissions.Add(PermissionEntries.Question("q1"));
            host.Settle();

            // A plain-text button (Allow, Deny, Submit…) yields its Content directly; an option
            // button's Content is the Label/Description StackPanel, so its first TextBlock stands in.
            var buttons = host.View.GetVisualDescendants().OfType<Button>()
                .Select(b => b.Content as string ?? b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text)
                .ToList();
            await Assert.That(buttons).Contains("Allow");
            await Assert.That(buttons).Contains("A");
            var otherBoxes = host.View.GetVisualDescendants().OfType<TextBox>()
                .Where(t => t.PlaceholderText == "Other…").ToList();
            await Assert.That(otherBoxes.Count).IsEqualTo(1);
            await Assert.That(otherBoxes[0].Classes.Contains("kcapField")).IsTrue();
            await Assert.That(host.View.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Pick")).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_question_hides_the_tool_group_and_shows_only_the_centered_card() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"AskUserQuestion","input":{"questions":[{"question":"declare this"}]}}]}}""",
            ]));
            host.Permissions.Add(PermissionEntries.Question("q1",
                toolInputJson: """{"questions":[{"question":"declare this","header":"Missing tools","options":[{"label":"A"}]}]}"""));
            await WaitUntilAsync(() => host.Chat.Items.OfType<PendingCardItem>().Any(), what: "the card");
            host.Settle();

            var group = host.View.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("toolGroup"));
            var card = host.View.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Classes.Contains("pendingCard"));
            await Assert.That(group.IsVisible).IsFalse();
            await Assert.That(((ToolGroupItem)group.DataContext!).SuppressedForPendingQuestion).IsTrue();
            await Assert.That(card.Classes.Contains("packsWithPrevious")).IsFalse();
            await Assert.That(card.Margin.Top).IsEqualTo(8);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_card_after_prose_keeps_the_paragraph_gap() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("prose.jsonl", [UserLine]));
            host.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => host.Chat.Items.OfType<PendingCardItem>().Any(), what: "the card");
            host.Settle();

            var card = host.View.GetVisualDescendants().OfType<ContentControl>().Single(c => c.Classes.Contains("pendingCard"));
            await Assert.That(card.Classes.Contains("packsWithPrevious")).IsFalse();
            await Assert.That(card.Margin.Top).IsEqualTo(8);
            await host.CloseAsync();
        });
    }

    /// The Other field is a real input in the virtualizing list: a click must keep caret focus
    /// through layout, so follow-tail cannot recycle the row out from under the caret.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Other_keeps_focus_on_click() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("tall.jsonl", Enumerable.Repeat(UserLine, 40).ToArray()));
            host.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => host.Chat.Items.OfType<PendingCardItem>().Any(), what: "the card");
            host.Settle();

            var other = host.View.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Other…");
            Click(host, other);
            host.Settle();
            await Assert.That(other.IsFocused).IsTrue();

            host.Window.KeyTextInput("mine");
            Dispatcher.UIThread.RunJobs();
            host.Settle();
            await Assert.That(other.IsFocused).IsTrue();
            await Assert.That(other.Text).IsEqualTo("mine");
            await Assert.That(((QuestionGroupViewModel)other.DataContext!).OtherText).IsEqualTo("mine");
            await host.CloseAsync();
        });
    }
    static Button Option(Host host, string label) => host.View.GetVisualDescendants().OfType<Button>()
        .Single(b => b.Classes.Contains("option") && b.DataContext is QuestionOptionViewModel { Label: var l } && l == label);
    static List<Button> Steps(Host host) => host.View.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("step")).ToList();
    static bool Shows(Host host, string text) => host.View.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_card_takes_focus_and_tabs_out() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Composer.Focus();
            host.Permissions.Add(PermissionEntries.Question("q1",
                toolInputJson: """{"questions":[{"question":"Pick","options":[{"label":"Ship it (Recommended)","description":"do it"},{"label":"Wait"},{"label":"Later"}]}]}"""));
            await WaitUntilAsync(() => host.Chat.PendingCards.Count == 1, what: "the card");
            host.Settle();

            await WaitUntilAsync(() => host.Window.FocusManager?.GetFocusedElement() is Button b
                && b.Classes.Contains("option") && b.Classes.Contains(":focus-visible")
                && b.DataContext is QuestionOptionViewModel { Label: "Ship it (Recommended)" }, what: "focus on the first option");
            var first = (Button)host.Window.FocusManager!.GetFocusedElement()!;
            var second = Option(host, "Wait");
            await Assert.That(first.Classes.Contains(":focus-visible")).IsTrue();
            await Assert.That(first.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Ship it (Recommended)" && t.IsEffectivelyVisible)).IsTrue();

            var presenter = first.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            await Assert.That(presenter.BorderBrush).IsSameReferenceAs(host.View.FindResource("KcapPurpleBrush"));

            host.Window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.Shift);
            var shifted = host.Window.FocusManager!.GetFocusedElement();
            await Assert.That(shifted is Button { Classes: var classes } && classes.Contains("option")).IsFalse();
            await Assert.That(shifted is TextBox { PlaceholderText: "Other…" }).IsFalse();
            first.Focus();

            host.Press(PhysicalKey.ArrowDown);
            await Assert.That(host.Window.FocusManager!.GetFocusedElement()).IsSameReferenceAs(second);
            await Assert.That(((QuestionOptionViewModel)second.DataContext!).IsSelected).IsFalse();
            await Assert.That(host.Chat.PendingCards.Count).IsEqualTo(1);

            host.Press(PhysicalKey.Digit1);
            await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsSameReferenceAs(first);
            await Assert.That(((QuestionOptionViewModel)first.DataContext!).IsSelected).IsFalse();

            host.Press(PhysicalKey.Tab);
            await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsSameReferenceAs(second);
            host.Press(PhysicalKey.Tab);
            host.Press(PhysicalKey.Tab);
            var other = host.View.GetVisualDescendants().OfType<TextBox>().Single(t => t.PlaceholderText == "Other…");
            await Assert.That(host.Window.FocusManager.GetFocusedElement()).IsSameReferenceAs(other);

            var focused = host.Window.FocusManager.GetFocusedElement();
            for (var i = 0; i < 8 && !ReferenceEquals(focused, host.Composer); i++) {
                host.Press(PhysicalKey.Tab);
                focused = host.Window.FocusManager.GetFocusedElement();
            }
            await Assert.That(focused).IsSameReferenceAs(host.Composer);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_and_back_move_focus_with_the_question() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Permissions.Add(PermissionEntries.Question("q1",
                toolInputJson: """{"questions":[{"question":"Tags","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]},{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}"""));
            host.Settle();
            var first = Option(host, "X");
            await WaitUntilAsync(() => ReferenceEquals(host.Window.FocusManager?.GetFocusedElement(), first), what: "the first option");
            Click(host, first);
            await WaitUntilAsync(() => ((QuestionOptionViewModel)first.DataContext!).IsSelected, what: "selected");

            var card = (QuestionCardViewModel)host.Chat.PendingCards.Single();
            var next = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Next");
            // Headless pointer events do not focus, so the focus a real activation gives the button is applied by hand.
            next.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            await WaitUntilAsync(() => card.CurrentIndex == 1, what: "the next question");
            host.Settle();
            var second = Option(host, "A");
            await WaitUntilAsync(() => ReferenceEquals(host.Window.FocusManager?.GetFocusedElement(), second), what: "focus on the next question");

            var back = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Back");
            back.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            await WaitUntilAsync(() => card.CurrentIndex == 0, what: "the previous question");
            host.Settle();
            await WaitUntilAsync(() => ReferenceEquals(host.Window.FocusManager?.GetFocusedElement(), Option(host, "X")), what: "focus on the previous question");
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Advancing_a_series_moves_focus_to_the_next_question() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Permissions.Add(PermissionEntries.Question("q1",
                toolInputJson: """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]},{"question":"Tags","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]}]}"""));
            host.Settle();
            var first = Option(host, "A");
            await WaitUntilAsync(() => ReferenceEquals(host.Window.FocusManager?.GetFocusedElement(), first), what: "the first option");

            Click(host, first);
            await WaitUntilAsync(() => ((QuestionCardViewModel)host.Chat.PendingCards.Single()).CurrentIndex == 1, what: "the next question");
            host.Settle();
            var next = Option(host, "X");
            await WaitUntilAsync(() => ReferenceEquals(host.Window.FocusManager?.GetFocusedElement(), next), what: "focus on the next question");
            await Assert.That(((QuestionOptionViewModel)next.DataContext!).IsSelected).IsFalse();
            await host.CloseAsync();
        });
    }

    /// A multi-select question toggles the selected class in place rather than advancing or submitting.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_picked_option_toggles_the_selected_class() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Permissions.Add(PermissionEntries.Question("q1",
                toolInputJson: """{"questions":[{"question":"Tags","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]}]}"""));
            host.Settle();
            var option = Option(host, "X");
            await Assert.That(option.Classes.Contains("selected")).IsFalse();

            Click(host, option);
            await WaitUntilAsync(() => option.Classes.Contains("selected"), what: "the selected class");
            await Assert.That(Option(host, "Y").Classes.Contains("selected")).IsFalse();

            Click(host, option);
            await WaitUntilAsync(() => !option.Classes.Contains("selected"), what: "cleared");
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_series_renders_one_question_with_step_chips_and_ends_on_a_review() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Permissions.Add(PermissionEntries.Question("q1",
                toolInputJson: """{"questions":[{"question":"Pick","header":"Choice","options":[{"label":"A"},{"label":"B"}]},{"question":"Tags","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]}]}"""));
            host.Settle();
            var card = (QuestionCardViewModel)host.Chat.PendingCards.Single();
            await Assert.That(Shows(host, "Pick")).IsTrue();
            await Assert.That(Shows(host, "Tags")).IsFalse();
            var chips = Steps(host);
            await Assert.That(chips.Select(c => ((QuestionStepViewModel)c.DataContext!).Title)).IsEquivalentTo(["Choice", "Question 2", "Review"], CollectionOrdering.Matching);
            await Assert.That(chips[0].Classes.Contains("current")).IsTrue();
            await Assert.That(host.View.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Submit" && b.IsEffectivelyVisible)).IsFalse();

            Click(host, Option(host, "A"));
            await WaitUntilAsync(() => card.CurrentIndex == 1, what: "advanced to the second question");
            host.Settle();
            await Assert.That(Shows(host, "Tags")).IsTrue();
            await Assert.That(Shows(host, "Pick")).IsFalse();
            chips = Steps(host);
            await Assert.That(chips[0].Classes.Contains("answered")).IsTrue();
            await Assert.That(chips[1].Classes.Contains("current")).IsTrue();

            Click(host, chips[2]);
            await WaitUntilAsync(() => card.IsOnReview, what: "the review step");
            host.Settle();
            await Assert.That(Shows(host, "Review your answers")).IsTrue();
            await Assert.That(Shows(host, "A")).IsTrue();
            await Assert.That(Shows(host, "Not answered")).IsTrue();
            var submit = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Submit");
            await Assert.That(submit.IsEffectivelyVisible).IsTrue();
            await Assert.That(submit.IsEffectivelyEnabled).IsFalse();
            await host.CloseAsync();
        });
    }

    /// A touch pan reaches the list as a scroll gesture, which routes on the bubbling strategy only;
    /// it must count as the reader's gesture like a wheel notch does, or every pan up is undone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_scroll_gesture_up_leaves_the_reader_where_they_panned() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("pan.jsonl", Enumerable.Repeat(UserLine, 60).ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            var items = host.View.FindControl<ItemsControl>("ChatItems")!;
            var row = items.GetRealizedContainers().First();
            row.RaiseEvent(new ScrollGestureEventArgs(ScrollGestureEventArgs.GetNextFreeId(), new Vector(0, -300)) { RoutedEvent = InputElement.ScrollGestureEvent });
            host.Settle();
            var panned = host.Scroll.Offset.Y;
            await Assert.That(host.AtBottom()).IsFalse();

            await host.AppendAndTickAsync(path, 20);
            host.Settle();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(panned);
            await host.CloseAsync();
        });
    }

    /// Pins the reader at the bottom across a question card's life. Marking the awaiting row
    /// changes its height, which makes the virtualizing panel drop its anchor and re-place every
    /// row from the average realized size; with tall prose above short rows that estimate is far
    /// off, so the extent collapses and the presenter clamps and anchor-shifts the offset. Neither
    /// move is the reader's, so the view must keep following through both the card's arrival and
    /// its retirement. The prose rows genuinely have to be tall — with uniform rows the estimate is
    /// exact and this test proves nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_card_arriving_and_retiring_keeps_the_reader_at_the_bottom() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var prose = string.Join("\\n\\n", Enumerable.Range(1, 60).Select(i => $"Paragraph {i} of a long reply that wraps across the column."));
            var tall = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + prose + "\"}]}}";
            const string question = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""";
            var ask = $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"q-tool","name":"AskUserQuestion","input":{{{question}}}}]}}""";
            var path = Tmp.CreateFile("card.jsonl", [tall, tall, tall, .. Enumerable.Repeat(UserLine, 20), ask]);
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            host.Permissions.Add(PermissionEntries.Entry("q1", "a1", "claude", ClaudeElicitation.ToolName, question, toolUseId: "q-tool"));
            await WaitUntilAsync(() => host.Chat.PendingCards.Count == 1, what: "the card");
            host.Settle();
            await Assert.That(host.AtBottom()).IsTrue();

            host.Permissions.Queue(PermissionResolveKind.Applied);
            var option = host.View.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("option"));
            // Headless pointer events do not focus, so the focus a real click gives the button is applied by hand.
            option.Focus(NavigationMethod.Pointer);
            Click(host, option);
            await WaitUntilAsync(() => host.Chat.PendingCards.Count == 0, what: "the card retired");
            host.Settle();
            host.Settle();

            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Pins that a trailing group hidden behind a question card whose card has not arrived does not
    /// loop layout. A zero-height last row is re-estimated at the average row size on every pass,
    /// so the extent grows each pass, follow-tail chases it, and the render loop gives up with
    /// "Infinite layout loop detected". The rows above genuinely have to be uneven — with uniform
    /// rows the estimate is exact and this test proves nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_waiting_for_its_card_does_not_loop_layout() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            try {
                var prose = string.Join("\\n\\n", Enumerable.Range(1, 60).Select(i => $"Paragraph {i} of a long reply that wraps across the column."));
                var tall = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + prose + "\"}]}}";
                const string question = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""";
                var ask = $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"q-tool","name":"AskUserQuestion","input":{{{question}}}}]}}""";
                var path = Tmp.CreateFile("pending-question.jsonl",
                    [tall, tall, tall, UserLine, ThinkingLine, CallLine(1), ResultLine(1), UserLine, ThinkingLine, CallLine(2), ResultLine(2),
                     ThinkingLine, CallLine(3), ResultLine(3), CallLine(4), ResultLine(4), ask]);
                await host.LoadAsync(path);
                await Assert.That(host.Chat.Items[^1] is ToolGroupItem { SuppressedForPendingQuestion: true }).IsTrue();

                var extent = host.Scroll.Extent.Height;
                for (var i = 0; i < 5; i++) {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                }

                await Assert.That(host.Scroll.Extent.Height).IsEqualTo(extent);
                await Assert.That(host.AtBottom()).IsTrue();
            } finally {
                await host.CloseAsync();
            }
        });
    }

    /// Pins the reader at the bottom across a series answer, by click and by Enter. Either is an
    /// input inside the list, and the card swaps to the next question in the same layout pass; that
    /// growth is the card's, not the reader scrolling, so following has to carry on and land the
    /// whole next question in view. The next question genuinely has to be taller — a shorter one
    /// clamps back to the bottom on its own and this test proves nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Answering_a_series_question_keeps_the_reader_at_the_bottom() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var prose = string.Join("\\n\\n", Enumerable.Range(1, 60).Select(i => $"Paragraph {i} of a long reply that wraps across the column."));
            var tall = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + prose + "\"}]}}";
            var tags = string.Join(",", Enumerable.Range(1, 8).Select(i =>
                $$$"""{"label":"Tag {{{i}}}","description":"A longer description of tag {{{i}}} that wraps across the column and adds height."}"""));
            var question = $$$"""{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]},{"question":"Tags","multiSelect":true,"options":[{{{tags}}}]}]}""";
            var ask = $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"q-tool","name":"AskUserQuestion","input":{{{question}}}}]}}""";
            var path = Tmp.CreateFile("series.jsonl", [tall, tall, tall, .. Enumerable.Repeat(UserLine, 20), ask]);
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            host.Permissions.Add(PermissionEntries.Entry("q1", "a1", "claude", ClaudeElicitation.ToolName, question, toolUseId: "q-tool"));
            await WaitUntilAsync(() => host.Chat.PendingCards.Count == 1, what: "the card");
            host.Settle();
            await Assert.That(host.AtBottom()).IsTrue();

            var card = (QuestionCardViewModel)host.Chat.PendingCards.Single();
            var option = Option(host, "A");
            // Headless pointer events do not focus, so the focus a real click gives the button is applied by hand.
            option.Focus(NavigationMethod.Pointer);
            Click(host, option);
            await WaitUntilAsync(() => card.CurrentIndex == 1, what: "the next question");
            host.Settle();
            host.Settle();
            await Assert.That(host.AtBottom()).IsTrue();

            var back = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Back");
            back.Focus(NavigationMethod.Tab);
            host.Press(PhysicalKey.Enter);
            await WaitUntilAsync(() => card.CurrentIndex == 0, what: "the first question again");
            host.Settle();
            Option(host, "A").Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            host.Press(PhysicalKey.Enter);
            await WaitUntilAsync(() => card.CurrentIndex == 1, what: "the next question by keyboard");
            host.Settle();
            host.Settle();

            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Page Up from a focused option reaches the ScrollViewer, which pages the list: that is the
    /// reader's own move, so following stops where they paged instead of snapping back to the card.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Paging_up_from_a_focused_option_leaves_the_reader_where_they_paged() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("page.jsonl", Enumerable.Repeat(UserLine, 60).ToArray()));
            host.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => host.Chat.PendingCards.Count == 1, what: "the card");
            host.Settle();
            await Assert.That(host.AtBottom()).IsTrue();
            var start = host.Scroll.Offset.Y;

            Option(host, "A").Focus(NavigationMethod.Tab);
            Dispatcher.UIThread.RunJobs();
            host.Press(PhysicalKey.PageUp);
            host.Settle();

            await Assert.That(host.AtBottom()).IsFalse();
            await Assert.That(host.Scroll.Offset.Y).IsLessThan(start - host.Scroll.Viewport.Height / 2);
            await host.CloseAsync();
        });
    }

    /// The chip strip's whole wiring: it is collapsed with an empty tray, a staged file renders a
    /// chip carrying its name and size, and the chip's own button takes that file back out.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Staged_chips_render_and_a_chip_removes_its_own_file() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("chips.jsonl", UserLine));
            var strip = host.View.FindControl<AttachmentChipStrip>("ChipStrip")!;
            await Assert.That(strip.IsVisible).IsFalse();

            host.Chat.Tray.AddAll([
                new StagedAttachment("a.png", "image/png", new byte[] { 1, 2, 3 }),
                new StagedAttachment("notes.txt", "text/plain", new byte[2048])]);
            host.Settle();

            await Assert.That(strip.IsVisible).IsTrue();
            await Assert.That(Chips(host).Select(c => c.FileName)).IsEquivalentTo(["a.png", "notes.txt"], CollectionOrdering.Matching);
            await Assert.That(Chips(host).Select(c => c.SizeLabel)).IsEquivalentTo(["3 B", "2 KB"], CollectionOrdering.Matching);

            var remove = strip.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ChipRemove");
            Click(host, remove);

            await Assert.That(host.Chat.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(["notes.txt"]);
            await Assert.That(Chips(host).Select(c => c.FileName)).IsEquivalentTo(["notes.txt"]);
            await host.CloseAsync();
        });
    }

    static List<StagedAttachmentViewModel> Chips(Host host) => host.View.GetVisualDescendants()
        .OfType<TextBlock>().Where(t => t.Name == "ChipName").Select(t => (StagedAttachmentViewModel)t.DataContext!).ToList();

    /// The hint the button carries exists only while the button is disabled, which is exactly when
    /// a tooltip is suppressed by default — and it arrives after the view is built, so the binding
    /// has to follow the channel rather than whatever the gate said at construction.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_attach_button_carries_its_hint_while_disabled() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.AttachAsync(Tmp.CreateFile("attach-hint.jsonl", UserLine));
            var button = host.View.FindControl<Button>("AttachButton")!;
            await Assert.That(ToolTip.GetShowOnDisabled(button)).IsTrue();

            host.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/2"]));
            host.Settle();
            await Assert.That(button.IsEnabled).IsFalse();
            await Assert.That(ToolTip.GetTip(button)).IsEqualTo("attachments aren't available for an in-place session");

            host.AllowAttachments();

            await Assert.That(button.IsEnabled).IsTrue();
            await Assert.That(ToolTip.GetTip(button)).IsNull();
            await host.CloseAsync();
        });
    }

    /// The view's half of the intake: a file dropped on the composer card reaches the tab's tray,
    /// and a text paste goes in through the TextBox exactly once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_drop_on_the_composer_stages_the_file_and_a_text_paste_lands_once() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.AttachAsync(Tmp.CreateFile("intake.jsonl", UserLine));
            host.AllowAttachments();
            await Assert.That(host.Chat.Attachments.CanAttach).IsTrue();

            var card = host.View.FindControl<Border>("ComposerCard")!;
            var resting = card.BorderBrush;
            await Assert.That(resting).IsNotNull();
            var transfer = new DataTransfer();
            var item = new DataTransferItem();
            item.SetFile(FakeStorageFile.Of("dropped.png", new byte[] { 1, 2, 3, 4 }));
            transfer.Add(item);

            card.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer, card, new Point(6, 6), KeyModifiers.None));
            host.Settle();
            // The highlight is a class the card's own style answers; a local brush on the card
            // would outrank it and the drag would look the same as no drag.
            await Assert.That(card.Classes.Contains("dragOver")).IsTrue();
            await Assert.That(card.BorderBrush).IsNotSameReferenceAs(resting);

            card.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, card, new Point(6, 6), KeyModifiers.None));
            await (host.View.PendingIntakeForTesting ?? Task.CompletedTask);
            host.Settle();

            await Assert.That(host.Chat.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(["dropped.png"]);
            await Assert.That(card.Classes.Contains("dragOver")).IsFalse();
            await Assert.That(card.BorderBrush).IsSameReferenceAs(resting);

            await TopLevel.GetTopLevel(host.Composer)!.Clipboard!.SetDataAsync(new FakeAsyncDataTransfer(text: "pasted text"));
            host.Composer.Focus();
            Dispatcher.UIThread.RunJobs();
            host.Composer.RaiseEvent(new RoutedEventArgs(TextBox.PastingFromClipboardEvent));
            await (host.View.PendingIntakeForTesting ?? Task.CompletedTask);
            host.Settle();

            await Assert.That(host.Composer.Text).IsEqualTo("pasted text");
            await Assert.That(host.Chat.ComposerText).IsEqualTo("pasted text");
            await Assert.That(host.Chat.Tray.Count).IsEqualTo(1);
            await host.CloseAsync();
        });
    }

    /// The banner names the one run with its pulsing dot while it lasts, stacks above the queue
    /// banner, and the activity note sits directly above the composer.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_subagents_banner_is_hidden_at_zero_and_names_the_run_above_the_queue_banner() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var banner = host.View.FindControl<Border>("SubagentsBanner")!;
            var queued = host.View.FindControl<Border>("QueuedMessagesBanner")!;
            var note = host.View.FindControl<TextBlock>("ChatActivityNote")!;
            var composer = host.View.FindControl<Border>("ComposerCard")!;
            var band = host.View.FindControl<Border>("ComposerBand")!;
            foreach (Control part in new Control[] { banner, queued, note, composer })
                await Assert.That(part.GetVisualAncestors().Contains(band)).IsTrue().Because($"{part.Name} belongs in the composer band");
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(Grid.GetRow(banner)).IsLessThan(Grid.GetRow(queued));
            await Assert.That(Grid.GetRow(queued)).IsLessThan(Grid.GetRow(note));
            await Assert.That(Grid.GetRow(note)).IsLessThan(Grid.GetRow(composer));

            var path = Tmp.CreateFile("sub.jsonl", [AgentCallLine, AgentLaunchLine]);
            await host.LoadAsync(path);
            await Assert.That(banner.IsVisible).IsTrue();
            var shown = banner.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            await Assert.That(shown).Contains("Explore");
            await Assert.That(shown.Any(t => t!.StartsWith("running in background · ", StringComparison.Ordinal))).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsTrue();
            await Assert.That(queued.IsVisible).IsFalse();

            await host.AppendLinesAndTickAsync(path, AgentFinishLine);
            await Assert.That(banner.IsVisible).IsFalse();
            await host.CloseAsync();
        });
    }

    /// A command's name can run to 80 characters; on a narrow pane the name gives way, never the
    /// state line with its timer. 550 is the chat pane at the window's minimum width, beside the
    /// rail and the work-context pane.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(600, true)]
    [Arguments(550, true)]
    [Arguments(550, false)]
    public async Task A_long_command_name_never_pushes_the_state_line_out_of_the_banner(double width, bool described) {
        await RunOnUiAsync(async () => {
            var host = new Host();
            host.Window.Width = width;
            var text = "Run every integration suite against the staging server and collect the coverage";
            var input = described ? $$$"""{"command":"make it","description":"{{{text}}}"}""" : $$$"""{"command":"{{{text}}}"}""";
            var call = $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_S","name":"Bash","input":{{{input}}}}]}}""";
            var launch = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_S","type":"tool_result","content":"Command running in background with ID: b1.","is_error":false}]},"toolUseResult":{"stdout":"","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false,"backgroundTaskId":"b1"}}""";
            await host.LoadAsync(Tmp.CreateFile("sh.jsonl", [call, launch]));
            host.Settle();

            var banner = host.View.FindControl<Border>("SubagentsBanner")!;
            var state = banner.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.IsEffectivelyVisible && t.Text!.StartsWith("running in background · ", StringComparison.Ordinal));
            var right = state.TranslatePoint(new Point(state.Bounds.Width, 0), banner)!.Value.X;
            await Assert.That(right).IsLessThanOrEqualTo(banner.Bounds.Width);
            await Assert.That(state.Bounds.Width).IsGreaterThan(0);
            await host.CloseAsync();
        });
    }
}

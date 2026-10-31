using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI.Reactive;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// ChatTabViewModel's tail: phases, the poll, item projection and pairing, path switches and
/// teardown. Every test runs under RunOnUiAsync (the apply hops through Dispatcher.UIThread) and
/// carries [NotInParallel("AvaloniaSession")], like every other VM suite touching the dispatcher.
public class ChatTabViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_remote_feed_switch_drains_after_the_previous_read_without_a_poll() => RunOnUiAsync(async () => {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("old"));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var remote = new RemoteTranscriptFeed("new", "gemini", access,
            (_, _) => Task.FromResult(new SessionDetailFetch(new SessionDetailDto { LastEventNumber = 0 })), lane, time, _ => { });
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), key => key == "old" ? new HeldFeed(entered, release) : remote,
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
        try {
            await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(entered.IsSet).IsTrue();
            var oldRead = chat.PendingReadForTesting!;
            session.OnNext(Session("new"));
            await WaitUntilAsync(() => lane.Tails.Count == 1, what: "new remote feed subscribed");
            lane.PushStreamEvent(RemoteFixtures.Envelope("new", 1, CanonicalEventTypes.AssistantTextGenerated, "{\"content\":\"live\"}"));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            release.Set();
            await oldRead;
            await WaitUntilAsync(() => chat.Items.OfType<AssistantTextItem>().Any(item => item.Text == "live"),
                what: "new rows drained without advancing the poll clock");
        } finally { release.Set(); await chat.TeardownAsync(); }
    });

    sealed class HeldFeed(ManualResetEventSlim entered, ManualResetEventSlim release) : IChatTranscriptFeed {
        public long? CurrentOffset => 0;
        public FeedRead ReadAppended() {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new(FeedStatus.Ok, []);
        }
        public void Dispose() { }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Remote_arrivals_drain_without_advancing_the_poll_clock_and_idle_ticks_do_not_read() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Establishing);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        var lane = new FakeServerLane { TailHandler = (_, _) => new Microsoft.AspNetCore.SignalR.HubException("not authorized") };
        var time = new FakeTimeProvider();
        var feed = new RemoteTranscriptFeed("s1", "gemini", access,
            (_, _) => Task.FromResult(new SessionDetailFetch(new SessionDetailDto { LastEventNumber = 0 })), lane, time, _ => { });
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time));
        try {
            access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => chat.Phase == ChatTabPhase.Failed, what: "refusal after the empty seed");
            lane.TailHandler = (_, _) => null;
            access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => lane.Tails.Count == 2 && chat.Phase == ChatTabPhase.Reading, what: "refusal cleared without a poll");
            var lastRead = chat.PendingReadForTesting;
            time.Advance(TimeSpan.FromSeconds(60));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            await Assert.That(ReferenceEquals(lastRead, chat.PendingReadForTesting)).IsTrue();
            for (var i = 1; i <= 20; i++) lane.PushStreamEvent(RemoteFixtures.Envelope("s1", (ulong)i,
                CanonicalEventTypes.AssistantTextGenerated, "{\"content\":\"live\"}"));
            await WaitUntilAsync(() => chat.Items.Count == 20, what: "all live arrivals drained without a poll");
            await Assert.That(chat.Items.OfType<AssistantTextItem>().All(item => item.Text == "live")).IsTrue();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_send_without_a_baseline_during_an_idle_read_still_gets_rebased() => RunOnUiAsync(async () => {
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        var feed = new GatedIdleFeed();
        var time = new FakeTimeProvider();
        var chat = new ChatTabViewModel("a1", AgentOrigin.Local, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time));
        try {
            await chat.PendingReadForTesting!;
            feed.BlockNext = true;
            time.Advance(ChatTabViewModel.PollInterval);
            await Task.Run(() => feed.Entered.Wait(TimeSpan.FromSeconds(5)));
            await Assert.That(feed.Entered.IsSet).IsTrue();
            feed.Offset = null;
            chat.ComposerText = "next";
            await chat.SendCommand.Execute();
            await Assert.That(chat.QueuedMessages.Single().HasBaseline).IsFalse();
            feed.Release.Set();
            await chat.PendingReadForTesting!;
            await Assert.That(chat.QueuedMessages.Single().HasBaseline).IsTrue();
        } finally {
            feed.Release.Set();
            await chat.PendingReadForTesting!;
            await chat.TeardownAsync();
        }
    });

    sealed class GatedIdleFeed : IChatTranscriptFeed {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool BlockNext;
        public long? Offset = 0;
        public long? CurrentOffset => Offset;
        public FeedRead ReadAppended() {
            if (BlockNext) {
                BlockNext = false;
                Entered.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return new(FeedStatus.Ok, [], SnapshotOffset: 10);
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""";
    const string ToolCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
    const string ToolResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string ToolErrorLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"boom","is_error":true}]}}""";
    const string MatchingQuestion = """{"questions":[{"question":"Run payload?","header":"Run payload","options":[{"label":"Yes"},{"label":"No"}]}]}""";
    const string OtherQuestion = """{"questions":[{"question":"Something else?","options":[{"label":"A"}]}]}""";
    const string QuestionCallLine = """{"type":"assistant","timestamp":"2026-08-28T10:00:00Z","message":{"content":[{"type":"tool_use","id":"qtool","name":"AskUserQuestion","input":{"questions":[{"question":"Run payload?","header":"Run payload","options":[{"label":"Yes"},{"label":"No"}]}]}}]}}""";
    const string QuestionResultLine = """{"type":"user","timestamp":"2026-08-28T10:00:01Z","message":{"content":[{"type":"tool_result","tool_use_id":"qtool","content":"The user did not answer the questions."}]}}""";
    const string QuestionAgainLine = """{"type":"assistant","timestamp":"2026-08-28T11:00:00Z","message":{"content":[{"type":"tool_use","id":"qtool2","name":"AskUserQuestion","input":{"questions":[{"question":"Run payload?","header":"Run payload","options":[{"label":"Yes"},{"label":"No"}]}]}}]}}""";
    const string QuestionAgainResultLine = """{"type":"user","timestamp":"2026-08-28T11:00:01Z","message":{"content":[{"type":"tool_result","tool_use_id":"qtool2","content":"The user did not answer the questions."}]}}""";
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string NoteLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""";
    const string ThinkingLine = """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"weighing it"}]}}""";
    const string PlanCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_P","name":"mcp__plugin_kcap_kcap-plans__update_plan_task","input":{"ordinal":1,"status":"completed"}}]}}""";
    const string PlanResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_P","content":"{}"}]}}""";
    const string PublishCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_pub","name":"mcp__plugin_kcap_kcap-artefacts__publish_artefact","input":{"title":"Retention brief","html":"<p>x</p>","visibility":"org"}}]}}""";
    const string PublishResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_pub","content":"{\"artefact\":{\"artefact_id\":\"01ec\",\"title\":\"Retention brief\",\"owner_user_id\":\"u1\",\"visibility\":\"org\",\"latest_version\":1,\"updated_at\":\"2026-10-07T10:00:00Z\",\"is_owner\":true,\"url\":\"https://kurrent.kcap.ai/artefacts/01ec\"}}"}]}}""";
    const string WorkItemCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_wi","name":"mcp__plugin_kcap_kcap-workitems__declare_work_item","input":{"issue_key":"WK-3084"}}]}}""";
    const string LinearCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_lin","name":"mcp__plugin_linear_linear__save_issue","input":{"title":"Add tests"}}]}}""";
    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string AgentLaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string ShellCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_S","name":"Bash","input":{"command":"dotnet test","description":"Run the full suite","run_in_background":true}}]}}""";
    const string ShellLaunchLine = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_S","type":"tool_result","content":"Command running in background with ID: bcyix00ks.","is_error":false}]},"toolUseResult":{"stdout":"","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false,"backgroundTaskId":"bcyix00ks"}}""";
    const string ShellFinishLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>bcyix00ks</task-id>\n<tool-use-id>toolu_S</tool-use-id>\n<status>completed</status>\n<summary>Background command \"Run the full suite\" completed (exit code 0)</summary>\n</task-notification>"}}""";
    const string AgentFinishLine ="""{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}}""";

    static AgentStatusDto Dto(string? transcriptPath, string vendor = "claude") =>
        Agent("a1", vendor, hasTerminal: true, repoPath: "/repo/x") with { TranscriptPath = transcriptPath };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_prompt_with_files_shows_its_typed_text_and_previews_only_its_images() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var prompt = JsonSerializer.Serialize("look\n\n[Attached files: .attached/b1/shot.png, .attached/b1/notes.pdf]");
            var path = Tmp.CreateFile("attached.jsonl", [UserLine.Replace("\"hello\"", prompt)]);
            await h.PushAsync(Dto(path) with { WorktreePath = Tmp.Path });

            var turn = (UserTurnItem)h.Chat.Items.Single();
            await Assert.That(turn.Text).IsEqualTo("look");
            await Assert.That(turn.Attachments.Select(a => a.FileName)).IsEquivalentTo(new[] { "shot.png", "notes.pdf" }, CollectionOrdering.Matching);
            await Assert.That(turn.Attachments[0].PreviewPath).IsEqualTo(Tmp.PathTo(".attached", "b1", "shot.png"));
            await Assert.That(turn.Attachments[1].PreviewPath).IsNull();

            h.Chat.OpenAttachmentCommand.Execute(turn.Attachments[1]).Subscribe();
            h.Chat.OpenAttachmentCommand.Execute(turn.Attachments[0]).Subscribe();
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { Tmp.PathTo(".attached", "b1", "shot.png") });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Image_previews_wait_for_successful_local_reads() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var read = ReadCallLine.Replace("/repo/x/src/a.cs", "images/a.png");
            var result = ToolResultLine.Replace("t1", "t2");
            var path = Tmp.CreateFile("images.jsonl", [read]);
            try {
                await h.PushAsync(Dto(path) with { WorktreePath = Tmp.Path });
                var call = Group(h.Chat, 0).Calls[0];
                await Assert.That(call.PreviewPath).IsNull();
                File.AppendAllText(path, result + "\n");
                await h.TickAsync();
                await Assert.That(call.PreviewPath).IsEqualTo(Tmp.PathTo("images", "a.png"));
                File.AppendAllText(path, read.Replace("t2", "t3") + "\n" + ToolErrorLine.Replace("t1", "t3") + "\n");
                await h.TickAsync();
                await Assert.That(Group(h.Chat, 0).Calls[^1].PreviewPath).IsNull();
            } finally {
                await h.TeardownAsync();
            }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Remote_reads_never_acquire_workstation_previews(bool useWindowsPath) {
        await RunOnUiAsync(async () => {
            var imagePath = useWindowsPath ? @"C:\Users\runneradmin\AppData\Local\Temp\images\a.png" : Tmp.PathTo("a.png");
            var path = Tmp.CreateFile("remote-image.jsonl", [
                ReadCallLine.Replace("\"/repo/x/src/a.cs\"", JsonSerializer.Serialize(imagePath)),
                ToolResultLine.Replace("t1", "t2"),
            ]);
            var time = new FakeTimeProvider();
            using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1") with { Root = Tmp.Path });
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                new AcceptingChatInput(), new NoAttachmentUploader(),
                _ => new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { }),
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            try {
                await (chat.PendingReadForTesting ?? throw new InvalidOperationException("The session did not start its transcript read."));
                await Assert.That(chat.Items.Count).IsEqualTo(1);
                var call = Group(chat, 0).Calls.Single();
                await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
                await Assert.That(ToolDetail.ImageFile(call.InputJson)).IsEqualTo(imagePath);
                await Assert.That(call.PreviewPath).IsNull();
            } finally {
                await chat.TeardownAsync();
            }
        });
    }

    static ToolGroupItem Group(ChatTabViewModel chat, int index) => (ToolGroupItem)chat.Items[index];
    static PendingCardItem[] CardRows(ChatTabViewModel chat) => [.. chat.Items.OfType<PendingCardItem>()];

    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTerminalAttachClientFactory Factory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public SessionRuns Runs { get; }
        public PlanActivity Plan { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }

        public Harness(IChatTranscriptProjection? projection, Action<FakePermissionService>? seed = null,
                       ChatInput? input = null, string? unavailableNote = null, IAttachmentUploader? uploader = null,
                       Action<ToolCard>? openCard = null, ComposerDrafts? drafts = null, LocalTranscripts? transcripts = null,
                       IObservable<AccountsSnapshotDto?>? accountReadings = null, IObservable<DateTimeOffset>? usageClock = null,
                       Action<string>? openLink = null) {
            seed?.Invoke(Permissions);
            Runs = new SessionRuns(Time);
            Terminal = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel(
                "a1", Daemon, input ?? new TerminalChatInput(Terminal, "a1", Daemon, new ScriptedLocalControlOps(), Observable.Never<AgentPresence>()), uploader ?? new NoAttachmentUploader(), projection, Opener, Time, Permissions, Runs, unavailableNote, planActivity: Plan, openCard: openCard, drafts: drafts, transcripts: transcripts,
                accountReadings: accountReadings, usageClock: usageClock, openLink: openLink);
        }

        public async Task PushAsync(AgentStatusDto dto) {
            Daemon.Agents.AddOrUpdate(dto);
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TickAsync() {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TeardownAsync() {
            await Chat.TeardownAsync();
            await Terminal.TeardownAsync();
        }
    }

    static Harness Claude(Action<FakePermissionService>? seed = null, Action<ToolCard>? openCard = null) =>
        new(TranscriptChat.For("claude"), seed, openCard: openCard);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_message_link_goes_to_the_session_link_handler_instead_of_the_browser() {
        await RunOnUiAsync(async () => {
            var routed = new List<string>();
            var h = new Harness(TranscriptChat.For("claude"), openLink: routed.Add);

            await h.Chat.OpenLinkCommand.Execute("https://a.kcap.ai/pages/abc");

            await Assert.That(routed).IsEquivalentTo(new[] { "https://a.kcap.ai/pages/abc" });
            await Assert.That(h.Opener.Opened).IsEmpty();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_already_cached_when_the_tab_opens_lights_the_row_at_once() {
        await RunOnUiAsync(async () => {
            var h = Claude(seed: p => p.Add(PermissionEntries.Entry("r1", "a1")));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the replayed card");
            await Assert.That(h.Chat.HasPendingCards).IsTrue();
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r1" });
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_closing_local_tab_hands_off_its_feed_whether_or_not_it_reads_through_the_shared_cache(bool shared) {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"), transcripts: shared ? new LocalTranscripts() : null);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine])));
            await WaitUntilAsync(() => h.Chat.Items.Count > 0, what: "the initial load");

            var handoff = h.Chat.ReleaseFeed();

            await Assert.That(handoff).IsNotNull();
            await Assert.That(handoff!.Runs).IsSameReferenceAs(h.Runs);
            handoff.Feed.Dispose();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Waits_until_a_path_then_renders_the_initial_load_in_file_order() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for chat…");

            await h.PushAsync(Dto(transcriptPath: null));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);

            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine, ToolCallLine]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(((UserTurnItem)h.Chat.Items[0]).Text).IsEqualTo("hello");
            await Assert.That(Group(h.Chat, 2).Calls[0].Detail).IsEqualTo("ls -la");
            await Assert.That(Group(h.Chat, 2).Calls[0].Outcome).IsEqualTo(ToolOutcome.Running);
            await Assert.That(Group(h.Chat, 2).Calls[0].Category).IsEqualTo(ToolCategory.Search);
            await h.TeardownAsync();
        });
    }

    /// Claude Code prints a thinking block as prose in its own terminal, so the chat shows the same
    /// text in file order and, like any prose, it closes the open tool group.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thinking_block_is_a_row_that_closes_the_open_group() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine, ThinkingLine, ReadCallLine]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(ToolGroupItem), nameof(AssistantThinkingItem), nameof(ToolGroupItem) },
                CollectionOrdering.Matching);
            await Assert.That(((AssistantThinkingItem)h.Chat.Items[2]).Text).IsEqualTo("weighing it");
            await Assert.That(Group(h.Chat, 1).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Bash" }, CollectionOrdering.Matching);
            await Assert.That(Group(h.Chat, 3).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Read" }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    /// Tool paths read relative to the checkout the agent runs in, which the wire names as
    /// worktree_path; the repository alone would leave a checkout outside its own directory, or
    /// one under `.claude/worktrees`, rendering absolute paths.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_paths_relativize_against_the_worktree_the_agent_runs_in() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            const string worktree = "/repo/x/.claude/worktrees/slug";
            const string readLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/.claude/worktrees/slug/src/Foo.cs"}}]}}""";
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Read", toolInputJson: """{"file_path":"/repo/x/.claude/worktrees/slug/src/Bar.cs"}"""));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the card");

            var path = Tmp.CreateFile("t.jsonl", [readLine]);
            await h.PushAsync(Dto(path) with { WorktreePath = worktree, WorkLocation = "owned" });

            await Assert.That(Group(h.Chat, 0).Calls.Single().Detail).IsEqualTo("src/Foo.cs");
            await WaitUntilAsync(() => ((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail == "src/Bar.cs", what: "relative to the worktree once the root lands");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Appended_lines_render_after_a_tick_and_a_partial_line_waits_for_its_newline() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine[..20]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.AppendAllText(path, ToolCallLine[20..] + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(3);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Tool_results_flip_their_call_in_place_and_unmatched_results_are_ignored() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));
            var call = Group(h.Chat, 0).Calls.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);

            File.AppendAllText(path, ToolCallLine + "\n" + ToolErrorLine + "\n" + ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
            await Assert.That(Group(h.Chat, 0).Calls[1].Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(Group(h.Chat, 0).Calls[1].IsError).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Length_regression_resets_items_missing_recovers_and_failed_keeps_items() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.PathTo("t.jsonl");
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Missing);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("The transcript file is missing");

            File.WriteAllLines(path, [UserLine, AssistantLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(h.Chat.Items[0]).IsTypeOf<ToolGroupItem>();

            File.Delete(path);
            Directory.CreateDirectory(path);
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            Directory.Delete(path);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Consecutive_calls_across_reads_share_a_group_and_any_prose_closes_it() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Bash", "Read" }, CollectionOrdering.Matching);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine.Replace("t1", "t3") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(Group(h.Chat, 2).Calls).Count().IsEqualTo(1);

            File.AppendAllText(path, NoteLine + "\n" + ToolCallLine.Replace("t1", "t4") + "\n" + UserLine + "\n" + ToolCallLine.Replace("t1", "t5") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] {
                nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem),
                nameof(SystemNoteItem), nameof(ToolGroupItem), nameof(UserTurnItem), nameof(ToolGroupItem),
            }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_result_folds_its_call_and_the_summary_follows_the_settled_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var group = Group(h.Chat, 0);
            await Assert.That(group.HasSummary).IsFalse();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(2);

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls.Select(c => c.Name)).IsEquivalentTo(new[] { "Read" });
            await Assert.That(group.Summary).IsEqualTo("Searched files");
            await Assert.That(group.HasSummary).IsTrue();
            await Assert.That(group.HasFailure).IsFalse();

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(1);

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).IsEmpty();
            await Assert.That(group.Summary).IsEqualTo("Searched files, read a file");
            await Assert.That(group.HasFailure).IsTrue();
            await Assert.That(group.IsExpanded).IsTrue();
            await h.TeardownAsync();
        });
    }

    /// Prose closes the group while the call stays pending. The result arrives in a later read,
    /// and the row it settles is already bound, so that update has to run on the UI thread.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_later_result_settles_the_closed_groups_call_on_the_ui_thread() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            var call = Group(h.Chat, 0).Calls[0];
            var onUiThread = false;
            call.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(ToolCallItem.Outcome))
                    onUiThread = Dispatcher.UIThread.CheckAccess();
            };

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();

            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(onUiThread).IsTrue();
            await Assert.That(Group(h.Chat, 0).Calls[0]).IsSameReferenceAs(call);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_and_a_path_switch_start_a_fresh_group_for_later_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(1);
            File.AppendAllText(other, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_kcap_call_reads_as_a_labelled_row_and_a_publish_becomes_a_card_when_its_result_lands() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [WorkItemCallLine, PublishCallLine]);
            await h.PushAsync(Dto(path));

            var group = Group(h.Chat, 0);
            var work = group.Calls[0];
            await Assert.That(work.Label).IsEqualTo("Attached work item");
            await Assert.That(work.Detail).IsEqualTo("WK-3084");
            await Assert.That(work.Category).IsEqualTo(ToolCategory.Work);
            var publish = group.Calls[1];
            await Assert.That(publish.Label).IsEqualTo("Published page");
            await Assert.That(publish.Detail).IsEqualTo("Retention brief");
            await Assert.That(publish.HasCard).IsFalse();

            File.AppendAllText(path, PublishResultLine + "\n");
            await h.TickAsync();

            await Assert.That(publish.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(publish.HasCard).IsTrue();
            await Assert.That(publish.Card!.Meta).IsEqualTo("v1 · Org");
            await Assert.That(publish.CanOpenCard).IsTrue();
            await publish.OpenCardCommand!.Execute();
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://kurrent.kcap.ai/artefacts/01ec" });
            await h.TeardownAsync();
        });
    }

    /// A described command keeps its description on the row under the same verb as any command,
    /// and opening it shows the command and the output the transcript recorded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_described_command_row_keeps_its_description_and_opens_on_the_recorded_command_and_output() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var call = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"rm -rf node_modules && npm ci","description":"Reinstall dependencies"}}]}}""";
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [call, ToolErrorLine])));

            var row = Group(h.Chat, 0).Calls[0];
            await Assert.That(row.Label).IsEqualTo("Ran command");
            await Assert.That(row.Detail).IsEqualTo("Reinstall dependencies");
            await Assert.That(row.Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(row.Result).IsEqualTo("boom");

            row.Toggle();
            var sections = row.Inspection!.Sections;
            await Assert.That(sections.Select(s => (s.Heading, s.Text))).IsEquivalentTo(new[] {
                ("Status", "Failed"), ("Command", "rm -rf node_modules && npm ci"), ("Output", "boom"),
            }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [Arguments("mcp__plugin_linear_linear__save_issue", "Linear · save issue", "Linear")]
    [Arguments("mcp__claude_ai_Linear__save_issue", "Linear · save issue", "Linear")]
    [Arguments("mcp__claude_ai_kcap-plans__some_tool", "Kcap plans · some tool", "Kcap plans")]
    [Arguments("mcp__claude_ai_kcap-plans__declare_plan_document", "Kcap plans · declare plan document", "Kcap plans")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_foreign_mcp_call_reads_as_server_and_tool_with_its_first_argument(string name, string label, string chip) {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var line = LinearCallLine.Replace("mcp__plugin_linear_linear__save_issue", name, StringComparison.Ordinal);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [line])));

            var group = Group(h.Chat, 0);
            var call = group.Calls[0];
            await Assert.That(group.KindChip).IsEqualTo(chip);
            await Assert.That(call.CardKind).IsEqualTo(ToolCardKind.None);
            await Assert.That(call.Label).IsEqualTo(label);
            await Assert.That(call.Detail).IsEqualTo("Add tests");
            await Assert.That(call.Category).IsEqualTo(ToolCategory.Other);
            await h.TeardownAsync();
        });
    }

    /// A page card follows the opener the workspace hands in and bypasses the browser.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_injected_card_opener_takes_precedence_over_the_browser() {
        await RunOnUiAsync(async () => {
            ToolCard? opened = null;
            var h = Claude(openCard: card => opened = card);
            var path = Tmp.CreateFile("t.jsonl", [PublishCallLine, PublishResultLine]);
            await h.PushAsync(Dto(path));

            var publish = Group(h.Chat, 0).Calls[0];
            await publish.OpenCardCommand!.Execute();
            await Assert.That(opened?.Url).IsEqualTo("https://kurrent.kcap.ai/artefacts/01ec");
            await Assert.That(h.Opener.Opened).IsEmpty();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_plan_write_in_the_transcript_is_reported_once_its_result_is_read() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var writes = 0;
            h.Plan.PlanWritten += () => writes++;
            var path = Tmp.CreateFile("t.jsonl", [PlanCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(writes).IsEqualTo(0);

            File.AppendAllText(path, PlanResultLine + "\n");
            await h.TickAsync();

            await Assert.That(writes).IsEqualTo(1);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_new_transcript_forgets_the_plan_writes_the_old_one_left_in_flight() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var writes = 0;
            h.Plan.PlanWritten += () => writes++;
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [PlanCallLine])));

            await h.PushAsync(Dto(Tmp.CreateFile("o.jsonl", [PlanResultLine])));

            await Assert.That(writes).IsEqualTo(0);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fixture_transcripts_drive_the_strip_singular_and_plural() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var raised = new List<string?>();
            h.Chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();
            await Assert.That(h.Chat.RunningRow!.Name).IsEqualTo("Explore");
            await Assert.That(h.Chat.RunningRow!.StateText).StartsWith("running in background · ");
            await Assert.That(h.Chat.RunSummary).IsEmpty();
            await Assert.That(h.Runs.Rows.Single().IsBackground).IsTrue();
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.HasRunningRuns));
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.RunningRow));
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.RunSummary));

            File.AppendAllText(path, AgentCallLine.Replace("toolu_A", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunningRow).IsNull();
            await Assert.That(h.Chat.RunSummary).IsEqualTo("2 subagents running");

            File.AppendAllText(path, AgentFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunningRow!.StateText).StartsWith("running · ");
            await Assert.That(h.Chat.RunSummary).IsEmpty();
            await Assert.That(h.Chat.Items.OfType<SystemNoteItem>().Count()).IsEqualTo(1);

            File.AppendAllText(path, ToolResultLine.Replace("t1", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await Assert.That(h.Runs.Rows.Select(r => r.State)).IsEquivalentTo(new[] { RunState.Done, RunState.Done }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fixture_transcripts_put_background_commands_in_the_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ShellCallLine, ShellLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();
            await Assert.That(h.Chat.RunningRow!.Name).IsEqualTo("Run the full suite");
            await Assert.That(h.Chat.RunningRow!.StateText).StartsWith("running in background · ");

            File.AppendAllText(path, ShellCallLine.Replace("toolu_S", "toolu_T") + "\n" + ShellLaunchLine.Replace("toolu_S", "toolu_T").Replace("bcyix00ks", "b2") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunningRow).IsNull();
            await Assert.That(h.Chat.RunSummary).IsEqualTo("2 commands running in background");

            File.AppendAllText(path, AgentCallLine + "\n" + AgentLaunchLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunSummary).IsEqualTo("1 subagent, 2 commands running in background");

            File.AppendAllText(path, ShellFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunSummary).IsEqualTo("1 subagent, 1 command running in background");
            await Assert.That(h.Runs.Rows.Single(r => r.CallId == "toolu_S").State).IsEqualTo(RunState.Done);

            File.AppendAllText(path, ShellCallLine.Replace("toolu_S", "toolu_F") + "\n" + ToolResultLine.Replace("t1", "toolu_F") + "\n");
            await h.TickAsync();
            await Assert.That(h.Runs.Rows.Any(r => r.CallId == "toolu_F")).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// A foreground launch is already a Task row while its turn is live, so the strip leaves it
    /// out until the turn is over.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_strip_leaves_a_foreground_run_to_the_transcript_while_a_turn_is_live() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var raised = new List<string?>();
            h.Chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for");
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await Assert.That(h.Chat.RunningRow).IsNull();

            raised.Clear();
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await Assert.That(h.Chat.ActivityNote).IsEmpty();
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();
            await Assert.That(h.Chat.RunningRow!.Name).IsEqualTo("Explore");
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.HasRunningRuns));
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.RunningRow));
            await h.TeardownAsync();
        });
    }

    /// The next prompt starts a live turn; a run still going in the background stays in the strip
    /// until it ends, as it stays in the sidebar.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_background_run_stays_in_the_strip_through_the_next_turn() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ShellCallLine, ShellLaunchLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();

            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for");
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();
            await Assert.That(h.Chat.RunningRow!.Name).IsEqualTo("Run the full suite");

            File.AppendAllText(path, AgentCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunningRow!.Name).IsEqualTo("Run the full suite");
            await Assert.That(h.Chat.RunSummary).IsEmpty();

            File.AppendAllText(path, ShellFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// A detach changes no state count, yet it is what brings a mid-turn run into the strip.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_run_sent_to_the_background_mid_turn_joins_the_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var raised = new List<string?>();
            h.Chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();

            raised.Clear();
            File.AppendAllText(path, AgentLaunchLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();
            await Assert.That(h.Chat.RunningRow!.StateText).StartsWith("running in background · ");
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.HasRunningRuns));
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_feed_reset_and_a_path_switch_empty_the_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();

            File.WriteAllLines(path, [UserLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await Assert.That(h.Runs.Rows).IsEmpty();

            var other = Tmp.CreateFile("o.jsonl", [AgentCallLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await Assert.That(h.Runs.Rows).IsEmpty();
            await h.TeardownAsync();
        });
    }

    /// The status can land before the first read: rows projected after the session ended present
    /// as stopped at once, and a reset under the ended session rebuilds them stopped too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_status_before_the_first_read_and_a_reset_after_it_leave_no_running_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path) with { Status = "Completed" });
            await Assert.That(h.Runs.Rows).Count().IsEqualTo(1);
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await Assert.That(h.Runs.Rows.Single().StateText).IsEqualTo("stopped");

            File.WriteAllLines(path, [AgentCallLine.Replace("Map desktop chat UI surfaces", "Map")]);
            await h.TickAsync();
            await Assert.That(h.Runs.Rows.Single().Description).IsEqualTo("Map");
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_generation_read_after_a_feed_switch_adds_no_subagent_rows() {
        await RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource();
            var h = new Harness(new TranscriptChatProjection(new GatedProjection(ClaudeTranscriptEvents.Instance, "OLD", gate), ClaudeChatRules.Instance));
            var oldPath = Tmp.CreateFile("old.jsonl", [AgentCallLine.Replace("Map desktop chat UI surfaces", "OLD")]);
            var newPath = Tmp.CreateFile("new.jsonl", [AssistantLine]);

            h.Daemon.Agents.AddOrUpdate(Dto(oldPath));
            await (h.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var oldRead = h.Chat.PendingReadForTesting!;

            h.Daemon.Agents.AddOrUpdate(Dto(newPath));
            gate.SetResult();
            await oldRead;
            await (h.Chat.PendingReadForTesting ?? Task.CompletedTask);
            await h.TickAsync();

            await Assert.That(h.Runs.Rows).IsEmpty();
            await Assert.That(h.Chat.HasRunningRuns).IsFalse();
            await h.TeardownAsync();
        });
    }

    sealed class GatedProjection(ITranscriptProjection inner, string blockOn, TaskCompletionSource gate) : ITranscriptProjection {
        public TranscriptContext CreateContext(string sessionId, string? agentId) => inner.CreateContext(sessionId, agentId);

        public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
            if (line.Contains(blockOn, StringComparison.Ordinal)) gate.Task.GetAwaiter().GetResult();
            return inner.Project(line, lineNumber, receivedAt, context);
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_path_switch_discards_a_read_still_in_flight_for_the_old_file() {
        await RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource();
            var h = new Harness(new TranscriptChatProjection(new GatedProjection(ClaudeTranscriptEvents.Instance, "OLD", gate), ClaudeChatRules.Instance));
            var oldPath = Tmp.CreateFile("old.jsonl", [UserLine.Replace("hello", "OLD"), ToolCallLine]);
            var newPath = Tmp.CreateFile("new.jsonl", [AssistantLine]);

            h.Daemon.Agents.AddOrUpdate(Dto(oldPath));
            await (h.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var oldRead = h.Chat.PendingReadForTesting!;

            h.Daemon.Agents.AddOrUpdate(Dto(newPath));
            gate.SetResult();
            await oldRead;
            await (h.Chat.PendingReadForTesting ?? Task.CompletedTask);
            await h.TickAsync();

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { nameof(AssistantTextItem) });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unavailable_for_a_vendor_without_a_projection_and_no_ticks_after_teardown() {
        await RunOnUiAsync(async () => {
            var unavailable = new Harness(projection: null);
            await Assert.That(unavailable.Chat.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(unavailable.Chat.PhaseNote).IsEqualTo("No chat view for this harness");
            await unavailable.TeardownAsync();

            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            await h.TeardownAsync();
            File.AppendAllText(path, AssistantLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            // A removed agent keeps its items.
            var kept = Claude();
            var keptPath = Tmp.CreateFile("kept.jsonl", [UserLine]);
            await kept.PushAsync(Dto(keptPath));
            kept.Daemon.Agents.Remove("a1");
            await Assert.That(kept.Chat.Items).Count().IsEqualTo(1);
            await kept.TeardownAsync();
        });
    }

    /// Thread identity, so deliberately not under WithImmediateRxScheduler. PhaseNote and the FIRST
    /// notification are what make it discriminating: that one is raised by the path switch, on
    /// whichever thread delivered the dto, while every later one comes from the apply, which hops
    /// to the UI thread on its own and would pass with no ObserveOn at all.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dto_pushed_from_a_pool_thread_lands_on_the_ui_thread() {
        var onUi = await DispatchAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine]);
            bool? phaseChangedOnUi = null;
            h.Chat.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(ChatTabViewModel.PhaseNote))
                    phaseChangedOnUi ??= Dispatcher.UIThread.CheckAccess();
            };

            await Task.Run(() => h.Daemon.Agents.AddOrUpdate(Dto(path)));
            await WaitUntilAsync(() => h.Chat.Phase == ChatTabPhase.Reading, what: "reading");
            await h.TeardownAsync();
            return phaseChangedOnUi;
        });
        await Assert.That(onUi).IsTrue();
    }

    /// Pins the system-note row: a task notification the vendor injects into the transcript
    /// renders as a system note carrying its summary and result, never as a user bubble.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_task_notification_renders_as_a_system_note() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""",
            ]);
            await h.PushAsync(Dto(path));

            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] { nameof(SystemNoteItem) });
            await Assert.That(((SystemNoteItem)h.Chat.Items[0]).Text).IsEqualTo("**Agent finished**\n\nAll good.");
            await h.TeardownAsync();
        });
    }

    /// Claude Code stores its compaction summary as a user record; the row must not read as the user's,
    /// and it starts folded because it restates the conversation above it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_compaction_summary_renders_as_a_conversation_compacted_note() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"user","isCompactSummary":true,"isVisibleInTranscriptOnly":true,"message":{"content":"This session is being continued from a previous conversation."}}""",
            ]);
            await h.PushAsync(Dto(path));

            var note = (SystemNoteItem)h.Chat.Items.Single();
            await Assert.That(note.Label).IsEqualTo(SystemNoteItem.CompactSummaryLabel);
            await Assert.That(note.Text).IsEqualTo("This session is being continued from a previous conversation.");
            await Assert.That(note.IsExpanded).IsFalse();
            await Assert.That(note.ToggleAccessibleName).IsEqualTo("Expand Conversation compacted");
            note.Toggle();
            await Assert.That(note.IsExpanded).IsTrue();
            await Assert.That(note.ToggleAccessibleName).IsEqualTo("Collapse Conversation compacted");
            note.Toggle();
            await Assert.That(note.IsExpanded).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// A subagent's hand-back is that agent's report, so its row names the agent rather than the user.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_subagent_handback_renders_as_a_subagent_report() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [
                """{"type":"attachment","attachment":{"type":"queued_command","commandMode":"prompt","prompt":"<agent-message from=\"a1\">\n[Subagent hand-back] Preamble. The report follows:\n  **Done.**\n</agent-message>","origin":{"kind":"peer","from":"a1"}}}""",
            ]);
            await h.PushAsync(Dto(path));

            var note = (SystemNoteItem)h.Chat.Items.Single();
            await Assert.That(note.Label).IsEqualTo(SystemNoteItem.AgentReportLabel);
            await Assert.That(note.IconKind).IsEqualTo(Lucide.Avalonia.LucideIconKind.BotMessageSquare);
            await Assert.That(note.Text).IsEqualTo("**Done.**");
            await Assert.That(note.IsExpanded).IsTrue();
            note.Toggle();
            await Assert.That(note.IsExpanded).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cards_are_filtered_to_the_agent_ordered_by_request_time_and_removed_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", requestedAt: "2026-08-28T10:00:02.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("rX", "other", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "two cards");
            await Assert.That(h.Chat.PendingCards.Select(c => c.RequestId).ToArray()).IsEquivalentTo(new[] { "r1", "r2" }, CollectionOrdering.Matching);
            await Assert.That(h.Chat.HasPendingCards).IsTrue();
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r1", "r2" }, CollectionOrdering.Matching);

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "one card left");
            await Assert.That(h.Chat.PendingCards[0].RequestId).IsEqualTo("r2");
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r2" });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pending_cards_sit_at_the_end_of_the_item_list_and_return_after_a_path_switch() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "card in the list");
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(PendingCardItem) }, CollectionOrdering.Matching);
            await Assert.That(CardRows(h.Chat)[0].Card.RequestId).IsEqualTo("r1");

            var other = Tmp.CreateFile("u.jsonl", [UserLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.Items.OfType<PendingCardItem>().Select(c => c.Card.RequestId)).IsEquivalentTo(new[] { "r1" });
            await Assert.That(h.Chat.Items[^1]).IsTypeOf<PendingCardItem>();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_card_packs_against_the_tool_group_it_follows_and_unpacks_when_cleared() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(group.PacksWithCard).IsTrue();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 0, what: "cleared");
            await Assert.That(group.PacksWithCard).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// The centered card owns a live question, so the group hides the moment the call is read —
    /// before the card exists. Waiting for the card would show the row for the round trip and
    /// then take it away. It comes back when the card retires, because until the transcript
    /// carries the result the row is the only record of the call.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_call_hides_its_group_before_the_card_arrives() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"AskUserQuestion","input":{"questions":[{"question":"declare this"}]}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(CardRows(h.Chat)).IsEmpty();
            await Assert.That(group.SuppressedForPendingQuestion).IsTrue();

            h.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            await Assert.That(group.SuppressedForPendingQuestion).IsTrue();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsFalse();

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 0, what: "cleared");
            await Assert.That(group.SuppressedForPendingQuestion).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_suppressed_question_group_returns_when_the_session_ends() {
        await RunOnUiAsync(async () => {
            var input = new AvailabilityInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"AskUserQuestion","input":{"questions":[{"question":"declare this"}]}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(group.SuppressedForPendingQuestion).IsTrue();

            input.SetAvailability(SendAvailability.Ended);
            await Assert.That(group.SuppressedForPendingQuestion).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_later_transcript_row_unpacks_the_group_the_card_left() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("ask.jsonl", [
                """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""",
            ]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            var group = (ToolGroupItem)h.Chat.Items[0];
            await Assert.That(group.PacksWithCard).IsTrue();

            File.AppendAllText(path, AssistantLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(PendingCardItem) }, CollectionOrdering.Matching);
            await Assert.That(group.PacksWithCard).IsFalse();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_card_after_prose_does_not_pack() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => CardRows(h.Chat).Length == 1, what: "the card");
            await Assert.That(h.Chat.Items.OfType<ToolGroupItem>().Any(g => g.PacksWithCard)).IsFalse();
            await Assert.That(CardRows(h.Chat)[0].PacksWithPrevious).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_permission_replayed_before_the_agent_dto_ends_up_with_a_relative_detail() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Read", toolInputJson: """{"file_path":"/repo/x/src/a.cs"}"""));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the card");
            await Assert.That(((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail).IsEqualTo("/repo/x/src/a.cs");
            await h.PushAsync(Dto(transcriptPath: null));
            await WaitUntilAsync(() => ((PermissionCardViewModel)h.Chat.PendingCards[0]).Detail == "src/a.cs", what: "relative once the root lands");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Question_entries_become_question_cards_beside_permission_cards() {
        await RunOnUiAsync(async () => {
            var h = Claude(p => {
                p.Add(PermissionEntries.Entry("r1", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
                p.Add(PermissionEntries.Question("q1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            });
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "both cards");
            await Assert.That(h.Chat.PendingCards[0]).IsTypeOf<PermissionCardViewModel>();
            await Assert.That(h.Chat.PendingCards[1]).IsTypeOf<QuestionCardViewModel>();
            await Assert.That(CardRows(h.Chat).Select(c => c.Card.GetType().Name)).IsEquivalentTo(
                new[] { nameof(PermissionCardViewModel), nameof(QuestionCardViewModel) }, CollectionOrdering.Matching);

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "question card removed");
            await Assert.That(h.Chat.PendingCards[0].RequestId).IsEqualTo("r1");
            await h.TeardownAsync();
        });
    }

    /// The header status reads the card type. Replacing a question with a permission leaves the
    /// collection nonempty, so the word has to move without waiting for the poll.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Resolving_a_question_beside_a_permission_updates_the_header_status() {
        await RunOnUiAsync(async () => {
            var h = Claude(p => p.Add(PermissionEntries.Entry("r1", "a1")));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the permission");
            await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.NeedsYou);

            h.Permissions.Add(PermissionEntries.Question("q1"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the question");
            await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.Answer);

            h.Permissions.Remove("q1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the question resolved");
            await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_with_an_id_marks_its_row_in_either_order_and_clears_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            var read = Group(h.Chat, 0).Calls[1];
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "the card-first mark");
            await Assert.That(read.IsAwaitingPermission).IsFalse();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !bash.IsAwaitingPermission, what: "cleared on resolve");

            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t2"));
            await WaitUntilAsync(() => read.IsAwaitingPermission, what: "the row-first mark");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();

            h.Permissions.Add(PermissionEntries.Entry("r3", "a1", toolUseId: "nope"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the unmatched card");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await Assert.That(read.IsAwaitingPermission).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Two_requests_on_one_row_keep_the_mark_until_both_go_and_a_settled_row_withdraws_the_survivor() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "marked");

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "one card left");
            await Assert.That(bash.IsAwaitingPermission).IsTrue();

            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(bash.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 0, what: "the survivor withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["r2"]);
            await h.TeardownAsync();
        });
    }

    /// The daemon never sees a terminal answer, so a result for a pending request's tool is the
    /// app's cue to retire it — whichever of the two arrives first, since a replayed request can
    /// land after the transcript's initial load. A request for another tool, or without an id,
    /// is left alone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_result_for_a_pending_requests_tool_withdraws_it_in_either_order() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t2"));
            h.Permissions.Add(PermissionEntries.Entry("r3", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 3, what: "three cards");

            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the result's request withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["r1"]);

            // Result already on file when the request arrives: withdrawn on arrival.
            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Add(PermissionEntries.Entry("r4", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == 2, what: "the late request withdrawn");
            await Assert.That(h.Permissions.Withdrawn[1]).IsEqualTo("r4");
            await Assert.That(h.Chat.PendingCards.Count).IsEqualTo(2);
            await h.TeardownAsync();
        });
    }

    /// AskUserQuestion's hook has no tool-use id, so the card is retired from the question text
    /// once that ask has a result. A different question, a plain prompt with no id, and a card
    /// requested after the result stay.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_settled_question_withdraws_matching_cards_that_have_no_tool_id() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("q.jsonl", [QuestionCallLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Question("q-match", toolInputJson: MatchingQuestion));
            h.Permissions.Add(PermissionEntries.Question("q-dup", toolInputJson: MatchingQuestion));
            h.Permissions.Add(PermissionEntries.Question("q-other", toolInputJson: OtherQuestion));
            h.Permissions.Add(PermissionEntries.Question("q-later", toolInputJson: MatchingQuestion, requestedAt: "2026-08-28T12:00:00.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("r-plain", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 5, what: "five cards");

            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, QuestionResultLine + "\n");
            await h.TickAsync();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 3, what: "the answered question withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["q-match", "q-dup"]);

            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Add(PermissionEntries.Question("q-late", toolInputJson: MatchingQuestion));
            await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == 3, what: "the late card withdrawn");
            await Assert.That(h.Permissions.Withdrawn[2]).IsEqualTo("q-late");
            await Assert.That(h.Chat.PendingCards.Count).IsEqualTo(3);
            await h.TeardownAsync();
        });
    }

    /// A second ask of the same question is still open, so a card for it is not retired by the
    /// earlier result. The result of that second ask is what retires it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_question_is_not_retired_by_an_earlier_ask_of_the_same_text() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("q.jsonl", [QuestionCallLine, QuestionResultLine, QuestionAgainLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Question("q-live", toolInputJson: MatchingQuestion, requestedAt: "2026-08-28T09:00:00.0000000+00:00"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the open question");
            await Assert.That(h.Permissions.Withdrawn).IsEmpty();

            h.Permissions.Queue(PermissionResolveKind.Applied);
            File.AppendAllText(path, QuestionAgainResultLine + "\n");
            await h.TickAsync();
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 0, what: "the second ask withdrawn");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["q-live"]);
            await h.TeardownAsync();
        });
    }

    /// An unparsed requested_at is the minimum timestamp, which is older than every settled ask.
    /// That is not evidence the card predates the result.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_question_whose_time_did_not_parse_is_not_retired_by_a_settled_ask() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("q.jsonl", [QuestionCallLine, QuestionResultLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Add(PermissionEntries.Question("q-bad", toolInputJson: MatchingQuestion, requestedAt: "not-a-timestamp"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the untimed question");
            await Assert.That(h.Permissions.Withdrawn).IsEmpty();
            await h.TeardownAsync();
        });
    }

    /// A withdraw the daemon could not be reached for is not final: it retries on its own after a
    /// backoff, with no other event needed, and a withdraw in flight is never sent twice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_withdraw_retries_on_its_own_after_a_backoff_and_is_never_doubled() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));

            var gate = h.Permissions.Arm();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == 1, what: "the first withdraw");
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t9"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "a reconcile while in flight");
            await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(1);

            gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
            await WaitUntilAsync(() => h.Chat.WithdrawsInFlightForTesting == 0, what: "the failure reopened it");
            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Time.Advance(ChatTabViewModel.WithdrawRetryDelay);
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the retry withdrew it");
            await Assert.That(h.Permissions.Withdrawn).IsEquivalentTo(["r1", "r1"]);
            await h.TeardownAsync();
        });
    }

    /// The backoff doubles and stops at the cap; after that only a fresh reconcile retries.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Withdraw_retries_are_capped_and_a_later_reconcile_tries_again() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));

            h.Permissions.Queue(PermissionResolveKind.TransportFailure, "daemon_unreachable");
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => h.Chat.WithdrawsInFlightForTesting == 0, what: "the first failure");
            for (var retry = 1; retry <= ChatTabViewModel.MaxWithdrawRetries; retry++) {
                h.Permissions.Queue(PermissionResolveKind.TransportFailure, "daemon_unreachable");
                h.Time.Advance(ChatTabViewModel.WithdrawRetryDelay * (1 << (retry - 1)) - TimeSpan.FromMilliseconds(1));
                await Task.Delay(20);
                await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(retry);
                h.Time.Advance(TimeSpan.FromMilliseconds(1));
                await WaitUntilAsync(() => h.Permissions.Withdrawn.Count == retry + 1, what: $"retry {retry}");
                await WaitUntilAsync(() => h.Chat.WithdrawsInFlightForTesting == 0, what: $"retry {retry} failed");
            }

            h.Time.Advance(TimeSpan.FromMinutes(5));
            await Task.Delay(20);
            await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(ChatTabViewModel.MaxWithdrawRetries + 1);

            h.Permissions.Queue(PermissionResolveKind.Applied);
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t9"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "the reconcile's retry withdrew it");
            await Assert.That(h.Permissions.Withdrawn.Count).IsEqualTo(ChatTabViewModel.MaxWithdrawRetries + 2);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_without_an_id_marks_the_sole_running_call_and_abstains_on_two() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", vendor: "codex"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var first = Group(h.Chat, 0).Calls[0];
            await WaitUntilAsync(() => first.IsAwaitingPermission, what: "the sole running call, row after card");

            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            var second = Group(h.Chat, 0).Calls[1];
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsFalse();

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !second.IsAwaitingPermission, what: "cleared on resolve");

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "a card with nothing running");
            await Assert.That(second.IsAwaitingPermission).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_request_marks_the_rebuilt_row_after_a_reset_and_a_path_switch() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => Group(h.Chat, 1).Calls[0].IsAwaitingPermission, what: "marked before the reset");

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine.Replace("t1", "t9")]);
            await h.PushAsync(Dto(other));
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r2");
            await WaitUntilAsync(() => !Group(h.Chat, 0).Calls[0].IsAwaitingPermission, what: "only the id-less request fitted t9");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_starts_a_fresh_projection_context_and_line_count() {
        await RunOnUiAsync(async () => {
            var counting = new CountingProjection(ClaudeTranscriptEvents.Instance);
            var h = new Harness(new TranscriptChatProjection(counting, ClaudeChatRules.Instance));
            var path = Tmp.CreateFile("t.jsonl", [UserLine, AssistantLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(counting.LineNumbers).IsEquivalentTo(new[] { 1, 2 });

            Tmp.CreateFile("t.jsonl", [UserLine]);    // shorter: the tail resets
            await h.TickAsync();
            await Assert.That(counting.LineNumbers).IsEquivalentTo(new[] { 1, 2, 1 });
            await Assert.That(counting.ContextsCreated).IsEqualTo(2);
            await h.TeardownAsync();
        });
    }

    /// A scripted ChatInput for composer tests: SendAsync completes when the test says so.
    sealed class ScriptedInput : ChatInput {
        public override string? SenderUserId => "u2";
        public TaskCompletionSource<ChatSendOutcome>? Pending;
        public int Disposals;
        public bool CanAttachValue = true;
        /// Off for a channel whose send has already gone out when the tab is torn down.
        public bool HonoursCancellation = true;
        public List<(string Text, IReadOnlyList<string> Ids, CancellationToken Ct)> Sends { get; } = [];
        public override SendAvailability Availability => Pending is null ? SendAvailability.Ready : SendAvailability.Sending;
        public override bool CanAcceptText => Pending is null;
        public override string Hint => "scripted";
        public override bool CanAttach => CanAttachValue;
        public override string? AttachHint => CanAttachValue ? null : "attachments need the daemon updated";
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
            Sends.Add((text, attachmentIds, ct));
            Pending = new TaskCompletionSource<ChatSendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.RaisePropertyChanged(nameof(CanAcceptText));
            return Pending.Task.ContinueWith(t => { Pending = null; this.RaisePropertyChanged(nameof(CanAcceptText)); return t.Result; }, HonoursCancellation ? ct : CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public override void Dispose() => Disposals++;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Journal_file_renders_user_assistant_note_and_tool_rows() {
        await RunOnUiAsync(async () => {
            var path = Tmp.CreateFile("j.jsonl", [
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SessionStarted, Cwd: "/w")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "hello")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SystemNote, Text: "note")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: "c1", ToolName: "Read", ToolInputJson: """{"file_path":"/w/a.cs"}""")),
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1", ToolResult: "ok")),
            ]);
            var h = new Harness(TranscriptChat.Journal);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { TranscriptPath = path, TranscriptFormat = TranscriptFormats.Envelopes });
            await h.TickAsync();

            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(SystemNoteItem), nameof(ToolGroupItem) },
                CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unavailable_note_words_the_older_and_newer_daemon_cases() {
        await RunOnUiAsync(async () => {
            var older = new Harness(null, unavailableNote: "Update the daemon to view this session");
            await Assert.That(older.Chat.Phase).IsEqualTo(ChatTabPhase.Unavailable);
            await Assert.That(older.Chat.PhaseNote).IsEqualTo("Update the daemon to view this session");
            await older.TeardownAsync();
            var plain = new Harness(null);
            await Assert.That(plain.Chat.PhaseNote).IsEqualTo("No chat view for this harness");
            await plain.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Send_clears_only_when_committed_and_the_text_is_unchanged() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(input.Sends.Single().Text).IsEqualTo("hello");
            h.Chat.ComposerText = "hello edited";
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("hello edited");

            h.Chat.ComposerText = "two";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("two");

            h.Chat.ComposerText = "three";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    /// The session is captured as each prompt goes out, and only a prompt the channel accepted can
    /// have answered its open questions, whether or not its tab outlived the send.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Only_an_accepted_send_reports_its_session_as_answered() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput { HonoursCancellation = false };
            var time = new FakeTimeProvider();
            var sending = new List<string>();
            var answered = new List<string>();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, new BehaviorSubject<ChatSessionInfo>(Session("s1")), Observable.Return<string[]?>(null), input,
                new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true }, new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time),
                sessionId: new BehaviorSubject<string?>("s1")) {
                InputSending = sid => { sending.Add(sid); return () => answered.Add(sid); },
            };
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "refused";
            var send = chat.SendCommand.Execute().ToTask();
            await Assert.That(sending).IsEquivalentTo(["s1"]);
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            await Assert.That(answered).IsEmpty();

            chat.ComposerText = "delivered";
            send = chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(answered).IsEquivalentTo(["s1"]);

            chat.ComposerText = "delivered after the tab closed";
            send = chat.SendCommand.Execute().ToTask();
            var pending = input.Pending!;
            await chat.TeardownAsync();
            pending.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(answered).IsEquivalentTo(["s1", "s1"]);
        });
    }

    /// While the local daemon reports another server, its session ids name that server's sessions,
    /// so a reply there answers nothing the app's tracker holds.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_send_reports_its_session_only_while_the_daemon_is_on_the_app_server() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var time = new FakeTimeProvider();
            var onAppServer = new BehaviorSubject<bool>(false);
            var sending = new List<string>();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Local, new BehaviorSubject<ChatSessionInfo>(Session("s1")), Observable.Return<string[]?>(null), input,
                new NoAttachmentUploader(), _ => new EmptyFeed(), new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time),
                sessionId: new BehaviorSubject<string?>("s1"), localDaemonOnAppServer: onAppServer) {
                InputSending = sid => { sending.Add(sid); return () => { }; },
            };

            chat.ComposerText = "elsewhere";
            var send = chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(sending).IsEmpty();

            onAppServer.OnNext(true);
            chat.ComposerText = "here";
            send = chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(sending).IsEquivalentTo(["s1"]);
            await chat.TeardownAsync();
        });
    }

    /// Pins that a draft outlives its tab, which navigation tears down, and that a sent one does not.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unsent_draft_survives_the_tab_being_rebuilt() {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput();
            var first = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            first.Chat.ComposerText = "half a thought";
            await first.TeardownAsync();

            var second = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            await Assert.That(second.Chat.ComposerText).IsEqualTo("half a thought");
            var send = second.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await second.TeardownAsync();

            var third = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            await Assert.That(third.Chat.ComposerText).IsEqualTo("");
            await third.TeardownAsync();
        });
    }

    /// Pins a send that settles after its tab is gone: only a confirmed delivery clears the draft —
    /// in the store and in the tab now showing it — and never over text typed since, in the old tab
    /// or the one that replaced it.
    [Test]
    [Arguments(ChatSendOutcome.Accepted, false, false, "")]
    [Arguments(ChatSendOutcome.Rejected, false, false, "hello")]
    [Arguments(ChatSendOutcome.Unconfirmed, false, false, "hello")]
    [Arguments(ChatSendOutcome.Accepted, true, false, "hello again")]
    [Arguments(ChatSendOutcome.Accepted, false, true, "hello there")]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_settling_after_teardown_clears_only_its_own_delivered_draft(
            ChatSendOutcome outcome, bool editedBeforeLeaving, bool editedInReplacement, string expected) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput { HonoursCancellation = false };
            var first = new Harness(TranscriptChat.Journal, input: input, drafts: drafts);
            first.Chat.ComposerText = "hello";
            var send = first.Chat.SendCommand.Execute().ToTask();
            var pending = input.Pending!;
            if (editedBeforeLeaving) first.Chat.ComposerText = "hello again";
            await first.TeardownAsync();

            var second = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            if (editedInReplacement) second.Chat.ComposerText = "hello there";
            pending.SetResult(outcome);
            await send;
            await Assert.That(second.Chat.ComposerText).IsEqualTo(expected);
            await second.TeardownAsync();

            var third = new Harness(TranscriptChat.Journal, input: new ScriptedInput(), drafts: drafts);
            await Assert.That(third.Chat.ComposerText).IsEqualTo(expected);
            await third.TeardownAsync();
        });
    }

    /// Pins that only a send the channel did not refuse counts as a submitted turn.
    [Test]
    [Arguments(ChatSendOutcome.Accepted, 1)]
    [Arguments(ChatSendOutcome.Unconfirmed, 1)]
    [Arguments(ChatSendOutcome.Rejected, 0)]
    [NotInParallel("AvaloniaSession")]
    public async Task TurnSubmitted_fires_only_for_a_send_the_channel_did_not_refuse(ChatSendOutcome outcome, int expected) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });
            var submitted = 0;
            h.Chat.TurnSubmitted += () => submitted++;

            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(submitted).IsEqualTo(0);
            input.Pending!.SetResult(outcome);
            await send;
            await Assert.That(submitted).IsEqualTo(expected);
            await h.TeardownAsync();
        });
    }

    /// A send that went out is recallable whether or not the transcript has echoed it yet; a
    /// rejected one never left the box, so there is nothing to recall.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_walks_the_sent_prompts_and_skips_a_rejected_send() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            h.Chat.ComposerText = "first";
            var send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            h.Chat.ComposerText = "refused";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            h.Chat.ComposerText = "second";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
            await send;
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            h.Chat.ComposerText = "";

            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("first");
            await Assert.That(h.Chat.RecallOlder()).IsFalse();
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Enter on a recalled copy of a queued prompt leaves that one row. Send on the row, once the
    /// agent is idle, is what delivers it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Enter_on_queued_text_does_not_add_a_second_row_and_send_delivers_it_once_idle() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            await Assert.That(queued.CanSendNow).IsTrue();
            await Assert.That(queued.PickupNote).IsEqualTo(QueuedChatMessage.IdlePickupNote);

            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("look");
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(input.Pending).IsNull();
            await Assert.That(input.Sends).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(queued);
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");

            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await Assert.That(queued.CanSendNow).IsFalse();
            await Assert.That(queued.PickupNote).IsEqualTo(QueuedChatMessage.AwaitingPickupNote);
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(input.Pending).IsNull();
            await Assert.That(input.Sends).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);

            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(input.Sends).Count().IsEqualTo(2);
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(queued);
            await Assert.That(h.Chat.QueuedMessages.Single().Text).IsEqualTo("look");
            await h.TeardownAsync();
        });
    }

    /// An idle row Send keeps an unrelated draft and leaves staged chips alone, including text
    /// typed while the channel is still deciding. The replacement is text-only, and a rejected
    /// attempt puts the original row back.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_keeps_draft_and_tray_and_restores_the_row_on_refusal() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            h.Chat.ComposerText = "unrelated draft";
            h.Chat.Tray.AddAll([new StagedAttachment("note.txt", "text/plain", "hi"u8.ToArray())]);

            var refused = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await refused;
            await Assert.That(input.Sends.Last().Ids).IsEmpty();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("unrelated draft");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(queued);
            await Assert.That(queued.CanSendNow).IsTrue();

            var accepted = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            await WaitUntilAsync(() => input.Pending is not null, what: "the queued send reaching the channel");
            await Assert.That(h.Chat.ComposerText).IsEqualTo("unrelated draft");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            h.Chat.ComposerText = "typed while sending";
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await accepted;
            await Assert.That(input.Sends.Last().Ids).IsEmpty();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("typed while sending");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(queued);
            await h.TeardownAsync();
        });
    }

    [Test]
    [Arguments(false, false, ChatSendOutcome.Accepted, false)]
    [Arguments(false, false, ChatSendOutcome.Unconfirmed, false)]
    [Arguments(false, false, ChatSendOutcome.Unconfirmed, true)]
    [Arguments(true, false, ChatSendOutcome.Accepted, false)]
    [Arguments(true, false, ChatSendOutcome.Unconfirmed, false)]
    [Arguments(true, false, ChatSendOutcome.Unconfirmed, true)]
    [Arguments(true, true, ChatSendOutcome.Accepted, false)]
    [Arguments(true, true, ChatSendOutcome.Unconfirmed, false)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_clears_only_an_unchanged_recall(bool recall, bool editBack, ChatSendOutcome outcome, bool echoFirst) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var queued = h.Chat.QueuedMessages.Single();
                if (recall) await Assert.That(h.Chat.RecallOlder()).IsTrue();
                else h.Chat.ComposerText = "look";
                if (editBack) {
                    h.Chat.ComposerText = "look again";
                    h.Chat.ComposerText = "look";
                }

                var send = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
                if (echoFirst) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                }
                input.Pending!.SetResult(outcome);
                await send;
                var ownsDraft = recall && !editBack;
                await Assert.That(h.Chat.ComposerText).IsEqualTo(ownsDraft && (echoFirst || outcome == ChatSendOutcome.Accepted) ? "" : "look");
                if (!echoFirst) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                }
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo(ownsDraft ? "" : "look");
                await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo(ownsDraft ? "" : "look");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Unconfirmed)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_retried_twice_is_retired_by_the_next_echo(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.QueuedMessages.Single();
                await SendAsync(h, input, "look");
                var other = h.Chat.QueuedMessages[^1];
                for (var attempt = 0; attempt < 2; attempt++) {
                    await Assert.That(retry.CanSendNow).IsTrue();
                    var send = h.Chat.SendQueuedCommand.Execute(retry).ToTask();
                    input.Pending!.SetResult(outcome);
                    await send;
                    retry = h.Chat.QueuedMessages[^1];
                }

                await Assert.That(input.Sends).Count().IsEqualTo(4);
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(other);
                await SendAsync(h, input, "look");
                for (var echo = 0; echo < 3; echo++) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                    await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(other);
                }
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted, false)]
    [Arguments(ChatSendOutcome.Accepted, true)]
    [Arguments(ChatSendOutcome.Unconfirmed, false)]
    [Arguments(ChatSendOutcome.Unconfirmed, true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_retry_echoes_preserve_a_send_between_retries(ChatSendOutcome outcome, bool echoBeforeRetry) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.SendQueuedCommand.Execute(h.Chat.QueuedMessages.Single()).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;
                var firstRetry = h.Chat.QueuedMessages.Single();

                h.Chat.ComposerText = "look";
                var independentSend = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(outcome);
                await independentSend;
                var independent = h.Chat.QueuedMessages[^1];
                if (echoBeforeRetry) File.AppendAllText(path, User("look") + "\n");
                retry = h.Chat.SendQueuedCommand.Execute(firstRetry).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;

                await Assert.That(input.Sends).Count().IsEqualTo(4);
                if (echoBeforeRetry) {
                    await h.TickAsync();
                    await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
                }
                for (var echo = 0; echo < (echoBeforeRetry ? 2 : 3); echo++) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                    var expectedDraft = outcome == ChatSendOutcome.Unconfirmed ? "look" : "";
                    await Assert.That(h.Chat.ComposerText).IsEqualTo(expectedDraft);
                    await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo(expectedDraft);
                    await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                    await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(independent);
                }
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Unconfirmed)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_after_reset_of_an_acknowledged_retry_is_retired_by_its_echo(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine, AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.SendQueuedCommand.Execute(h.Chat.QueuedMessages.Single()).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();

                File.WriteAllLines(path, [AssistantLine]);
                await h.TickAsync();
                h.Chat.ComposerText = "look";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(outcome);
                await send;
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_second_retry_refusal_preserves_echo_ownership(bool echoDuringRetry) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var retry = h.Chat.SendQueuedCommand.Execute(h.Chat.QueuedMessages.Single()).ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await retry;
                var firstRetry = h.Chat.QueuedMessages.Single();

                h.Chat.ComposerText = "look";
                var independentSend = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await independentSend;
                var independent = h.Chat.QueuedMessages[^1];
                retry = h.Chat.SendQueuedCommand.Execute(firstRetry).ToTask();
                if (echoDuringRetry) {
                    File.AppendAllText(path, User("look") + "\n");
                    await h.TickAsync();
                }
                input.Pending!.SetResult(ChatSendOutcome.Rejected);
                await retry;
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(echoDuringRetry ? 1 : 2);
                if (!echoDuringRetry) await Assert.That(h.Chat.QueuedMessages[^1]).IsSameReferenceAs(firstRetry);
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look");

                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(independent);
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look");
                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(ChatSendOutcome.Accepted)]
    [Arguments(ChatSendOutcome.Rejected)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_retries_unconfirmed_text_only_when_idle(ChatSendOutcome outcome) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "look";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await send;
                var queued = h.Chat.QueuedMessages.Single();
                await Assert.That(queued.IsUnconfirmed).IsTrue();
                await Assert.That(queued.CanSendNow).IsTrue();
                await h.PushAsync(Dto(path) with { Status = "Running", AwaitingInput = false });
                await Assert.That(queued.CanSendNow).IsFalse();
                await h.Chat.SendQueuedCommand.Execute(queued);
                await Assert.That(input.Sends).Count().IsEqualTo(1);

                await h.PushAsync(Dto(path) with { Status = "Running", AwaitingInput = true });
                var retry = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
                input.Pending!.SetResult(outcome);
                await retry;
                var remaining = h.Chat.QueuedMessages.Single();
                if (outcome == ChatSendOutcome.Rejected) await Assert.That(remaining).IsSameReferenceAs(queued);
                else await Assert.That(remaining).IsNotSameReferenceAs(queued);
                await Assert.That(remaining.IsUnconfirmed).IsEqualTo(outcome == ChatSendOutcome.Rejected);
                await Assert.That(remaining.CanSendNow).IsTrue();
                await Assert.That(h.Chat.ComposerText).IsEqualTo(outcome == ChatSendOutcome.Accepted ? "" : "look");

                File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_refusal_does_not_restore_an_echoed_prompt(bool echoedBeforeRetry) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path));
                await SendAsync(h, input, "look");
                var queued = h.Chat.QueuedMessages.Single();
                h.Chat.ComposerText = "look";
                if (echoedBeforeRetry) File.AppendAllText(path, User("look") + "\n");
                var retry = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
                if (!echoedBeforeRetry) File.AppendAllText(path, User("look") + "\n");
                await h.TickAsync();
                input.Pending!.SetResult(ChatSendOutcome.Rejected);
                await retry;

                await Assert.That(input.Sends).Count().IsEqualTo(2);
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Two same-text rows plus an unedited recall must not block an explicit Send on one of them.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_bypasses_recall_dedup_when_another_same_text_row_remains() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            await SendAsync(h, input, "look");
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
            var first = h.Chat.QueuedMessages[0];
            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("look");

            var forced = h.Chat.SendQueuedCommand.Execute(first).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(input.Sends).Count().IsEqualTo(3);
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
            await Assert.That(h.Chat.QueuedMessages.Any(q => ReferenceEquals(q, first))).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    /// The resend's own echo retires the replacement. A later copy of that text is the dropped
    /// row's, so the banner stays clear.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_resend_echo_retires_the_replacement_and_a_later_copy_is_absorbed() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
            await Assert.That(h.Chat.QueuedMessages.Single()).IsNotSameReferenceAs(queued);

            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Once the resend has echoed, a later copy of its text is absorbed. Another row that happens
    /// to carry the same text stays queued.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_later_echo_of_a_resent_row_leaves_another_same_text_row_queued() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            await SendAsync(h, input, "look");
            var first = h.Chat.QueuedMessages[0];
            var second = h.Chat.QueuedMessages[1];
            var forced = h.Chat.SendQueuedCommand.Execute(first).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;

            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(second);
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.QueuedMessages.Single()).IsSameReferenceAs(second);
            await h.TeardownAsync();
        });
    }

    /// A send made after the resend has echoed owns that echo. The dropped row does not take it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_after_a_resent_row_echoed_is_retired_by_its_own_echo() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();

            h.Chat.ComposerText = "look";
            var again = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await again;
            await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
            File.AppendAllText(path, User("look") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Teardown disposes the busy signal while the queued send is still awaiting the channel.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_during_a_queued_send_preserves_independent_drafts(bool recall) {
        await RunOnUiAsync(async () => {
            var drafts = new ComposerDrafts();
            var input = new ScriptedInput { HonoursCancellation = false };
            var h = new Harness(TranscriptChat.For("claude"), input: input, drafts: drafts);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            if (recall) await Assert.That(h.Chat.RecallOlder()).IsTrue();
            else h.Chat.ComposerText = "look";
            var forced = h.Chat.SendQueuedCommand.Execute(queued).ToTask();
            await WaitUntilAsync(() => input.Pending is not null, what: "the queued send reaching the channel");
            await h.TeardownAsync();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await forced;
            await Assert.That(drafts.Get(AgentOrigin.Local, "a1")).IsEqualTo(recall ? "" : "look");
        });
    }

    /// A send still uploading keeps CanAcceptText true; the busy gate hides row Send until it ends.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_send_is_hidden_while_another_send_is_uploading() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var uploader = new HoldingUploader();
            var h = new Harness(TranscriptChat.For("claude"), input: input, uploader: uploader);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "look");
            var queued = h.Chat.QueuedMessages.Single();
            await Assert.That(queued.CanSendNow).IsTrue();

            h.Chat.ComposerText = "with file";
            h.Chat.Tray.AddAll([new StagedAttachment("shot.png", "image/png", new byte[] { 1 })]);
            var send = h.Chat.SendCommand.Execute().ToTask();
            await uploader.Started.Task;
            await Assert.That(queued.CanSendNow).IsFalse();
            await h.Chat.SendQueuedCommand.Execute(queued);
            await Assert.That(input.Sends).Count().IsEqualTo(1);

            uploader.Release.SetResult(new UploadOutcome(UploadKind.Uploaded, ["file-1"], null));
            await WaitUntilAsync(() => input.Pending is not null, what: "the send reaching the channel");
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(queued.CanSendNow).IsTrue();
            await h.TeardownAsync();
        });
    }

    /// Text equality cannot prove a recall was left alone: an edit undone by hand lands on the
    /// same string, and that string is the user's own draft now.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_ends_when_a_recalled_prompt_is_edited_and_restored() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            foreach (var text in new[] { "first", "second" }) {
                h.Chat.ComposerText = text;
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
            }
            h.Chat.ComposerText = "";
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");

            h.Chat.ComposerText = "second!";
            h.Chat.ComposerText = "second";
            await Assert.That(h.Chat.RecallOlder()).IsFalse();
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("second");
            await h.TeardownAsync();
        });
    }

    /// <summary>One poll that reads what is on disk now. A tick that lands while an earlier read is still
    /// in flight starts nothing, and that earlier read may have missed the newest line, so it is drained
    /// first.</summary>
    static async Task PollAsync(ChatTabViewModel chat, FakeTimeProvider time) {
        await (chat.PendingReadForTesting ?? Task.CompletedTask);
        time.Advance(ChatTabViewModel.PollInterval);
        await (chat.PendingReadForTesting ?? Task.CompletedTask);
    }

    static string User(string text) => UserLine.Replace("hello", text);

    static async Task AssertRecallOrder(ChatTabViewModel chat, params string[] newestFirst) {
        chat.ComposerText = "";
        foreach (var expected in newestFirst) {
            await Assert.That(chat.RecallOlder()).IsTrue();
            await Assert.That(chat.ComposerText).IsEqualTo(expected);
        }
        await Assert.That(chat.RecallOlder()).IsFalse();
    }

    static async Task SendAsync(Harness h, ScriptedInput input, string text) {
        h.Chat.ComposerText = text;
        var send = h.Chat.SendCommand.Execute().ToTask();
        input.Pending!.SetResult(ChatSendOutcome.Accepted);
        await send;
    }

    /// A workspace opened on an existing session recalls what its transcript records as
    /// submitted, slash commands included, and a rewritten transcript replaces it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_reaches_the_input_the_transcript_records() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            const string compact = """{"type":"user","message":{"content":"<command-name>/compact</command-name>"}}""";
            var path = Tmp.CreateFile("t.jsonl", [User("first"), AssistantLine, compact, UserLine]);
            await h.PushAsync(Dto(path));
            await AssertRecallOrder(h.Chat, "hello", "/compact", "first");

            File.WriteAllLines(path, [User("rewritten")]);
            await h.TickAsync();
            await AssertRecallOrder(h.Chat, "rewritten");
            await h.TeardownAsync();
        });
    }

    /// A send that lands before the read carrying older turns stays newest, and its echo moves it
    /// into the transcript's entries rather than adding a second copy.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_awaiting_its_echo_is_newest_and_its_echo_does_not_double_it() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            File.AppendAllText(path, User("old") + "\n");
            await SendAsync(h, input, "new");
            await h.TickAsync();
            await AssertRecallOrder(h.Chat, "new", "old");

            File.AppendAllText(path, User("new") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await AssertRecallOrder(h.Chat, "new", "old");
            await h.TeardownAsync();
        });
    }

    /// A replay can carry an older turn with the same text as a send still in flight; that turn is
    /// history, not the send's echo.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_replayed_turn_does_not_settle_a_newer_send_with_the_same_text() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [User("dropped"), User("a"), User("b")]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "a");

            File.WriteAllLines(path, [User("a"), User("b")]);
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
            await AssertRecallOrder(h.Chat, "a", "b", "a");
            await h.TeardownAsync();
        });
    }

    /// Claude records a slash command as its name and trimmed arguments joined by one space.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_slash_command_sent_with_extra_spacing_is_acknowledged_by_its_receipt() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path));
            await SendAsync(h, input, "/review   1504 ");
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();

            File.AppendAllText(path, """{"type":"user","message":{"content":"<command-name>/review</command-name><command-args>1504</command-args>"}}""" + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await AssertRecallOrder(h.Chat, "/review 1504");
            await h.TeardownAsync();
        });
    }

    /// Another transcript recalls nothing from the last, and a recall on screen becomes a draft.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Another_transcript_recalls_nothing_sent_while_reading_the_last() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [User("before")])));
            await SendAsync(h, input, "unechoed");
            await Assert.That(h.Chat.RecallOlder()).IsTrue();

            await h.PushAsync(Dto(Tmp.CreateFile("o.jsonl", [User("other")])));
            await Assert.That(h.Chat.RecallNewer()).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("unechoed");
            await AssertRecallOrder(h.Chat, "other");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Recall_leaves_out_the_attachment_trailer_the_daemon_appended() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var withFiles = """{"type":"user","message":{"role":"user","content":"look at this\n\n[Attached files: /x/a.png]"}}""";
            await h.PushAsync(Dto(Tmp.CreateFile("t.jsonl", [withFiles])));
            await AssertRecallOrder(h.Chat, "look at this");
            await h.TeardownAsync();
        });
    }

    /// Text alone cannot decide this: an edit during the round trip that ends on the sent text is
    /// still the user's own draft, and clearing it would erase what they typed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Text_edited_back_to_the_sent_value_during_the_round_trip_survives() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            h.Chat.ComposerText = "hello!";
            h.Chat.ComposerText = "hello";
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;

            await Assert.That(h.Chat.ComposerText).IsEqualTo("hello");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_cancels_an_in_flight_send_before_disposing_the_input_once() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });
            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            var ct = input.Sends.Single().Ct;
            await Assert.That(ct.IsCancellationRequested).IsFalse();

            await h.TeardownAsync();

            await Assert.That(ct.IsCancellationRequested).IsTrue();
            await Assert.That(input.Disposals).IsEqualTo(1);
            input.Pending?.TrySetCanceled(ct);
            try { await send; } catch (OperationCanceledException) { }
        });
    }

    static AgentStatusDto Hosted(string path, string status, bool? awaitingInput) =>
        Agent("a1", "pi", hasTerminal: false) with {
            TranscriptPath = path, TranscriptFormat = TranscriptFormats.Envelopes, Status = status, AwaitingInput = awaitingInput,
        };

    /// Busy state comes from the daemon, so the note remains visible before and during output.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Activity_note_reads_starting_then_working_and_clears_when_the_agent_waits() {
        await RunOnUiAsync(async () => {
            var path = Tmp.CreateFile("j.jsonl", [
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SessionStarted, Cwd: "/w")),
            ]);
            var h = new Harness(TranscriptChat.Journal);

            await h.PushAsync(Hosted(path, "Starting", awaitingInput: false));
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Starting Pi…");

            await h.PushAsync(Hosted(path, "Running", awaitingInput: false));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");

            File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi")) + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for ");

            // The awaiting-input flag hides it (the turn ended and the agent waits on the user)…
            await h.PushAsync(Hosted(path, "Running", awaitingInput: true));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            await h.PushAsync(Hosted(path, "Running", awaitingInput: false));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");

            // Output does not end the timer; only a turn verdict does.
            File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "on it")) + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for ");
            await h.TeardownAsync();
        });
    }

    /// Navigating away and back builds a new pane mid-turn; it shows the turn's real elapsed time.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pane_opened_mid_turn_counts_from_the_daemons_turn_start() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            var started = h.Time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(65);
            var dto = Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false, WorkingSince = started };
            await h.PushAsync(dto);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            h.Time.Advance(TimeSpan.FromSeconds(10));
            await h.PushAsync(dto);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 15s");
            await h.TeardownAsync();
        });
    }

    /// A pane opened behind a pending card still excludes the time the card stays up.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pane_opened_behind_a_card_excludes_the_blocked_time() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                var started = h.Time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(65);
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false, WorkingSince = started });
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                h.Time.Advance(TimeSpan.FromMinutes(10));
                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Working_time_ticks_without_a_transcript_and_resets_for_each_turn() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            var dto = Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false };
            await h.PushAsync(dto);
            h.Time.Advance(TimeSpan.FromSeconds(65));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            await h.PushAsync(dto); // repeated snapshots must not restart the clock
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            await h.PushAsync(dto with { AwaitingInput = true });
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await h.PushAsync(dto);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");
            await h.PushAsync(dto with { AwaitingInput = null });
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            await h.PushAsync(dto);
            h.Daemon.Agents.Remove("a1");
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            await h.TeardownAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Queued_messages_ignore_old_echoes_and_acknowledge_repeated_text_one_at_a_time() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("queued.jsonl", [UserLine]);
            await h.PushAsync(Dto(path));
            // Unread history with identical text is older than both sends.
            File.AppendAllText(path, UserLine + "\n");
            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            h.Chat.ComposerText = "hello";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await h.TickAsync();
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("2 messages queued");
            File.AppendAllText(path, UserLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            File.AppendAllText(path, UserLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// The channel holds an attachment send while the daemon fetches the files: until the channel
    /// takes it, the prompt is in the composer and nowhere else.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_attachment_send_waiting_on_the_channel_shows_only_in_the_composer() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var uploader = new HoldingUploader();
            var h = new Harness(TranscriptChat.For("claude"), input: input, uploader: uploader);
            try {
                var path = Tmp.CreateFile("pasted.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "look at this";
                h.Chat.Tray.AddAll([new StagedAttachment("shot.png", "image/png", new byte[] { 1 })]);
                var send = h.Chat.SendCommand.Execute().ToTask();
                await uploader.Started.Task;
                uploader.Release.SetResult(new UploadOutcome(UploadKind.Uploaded, ["file-1"], null));
                await WaitUntilAsync(() => input.Pending is not null, what: "the send reaching the channel");
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("look at this");

                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A prompt Claude Code takes up mid-turn is recorded only as a queued_command attachment:
    /// that record is its echo, and the chat shows it as the user's turn.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_prompt_taken_up_mid_turn_leaves_the_queue_and_shows_as_a_user_turn() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("absorbed.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "also check the toggle";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");

                File.AppendAllText(path, """{"type":"attachment","attachment":{"type":"queued_command","prompt":"also check the toggle","commandMode":"prompt","origin":{"kind":"human"}}}""" + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.Items.OfType<UserTurnItem>().Single().Text).IsEqualTo("also check the toggle");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_echo_before_the_send_ack_is_not_requeued_and_a_refusal_keeps_the_draft() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var path = Tmp.CreateFile("queued.jsonl", []);
            await h.PushAsync(Dto(path));
            h.Chat.ComposerText = "hello";
            var send = h.Chat.SendCommand.Execute().ToTask();
            File.AppendAllText(path, UserLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            h.Chat.ComposerText = "refused";
            send = h.Chat.SendCommand.Execute().ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("refused");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_path_switch_keeps_unconfirmed_input_until_a_fresh_echo() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("before.jsonl", [UserLine]);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                var other = Tmp.CreateFile("after.jsonl", [UserLine]);
                await h.PushAsync(Dto(other));
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                File.AppendAllText(other, UserLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_rebases_input_without_acknowledging_replayed_history() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("reset.jsonl", [UserLine, AssistantLine, AssistantLine]);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                File.WriteAllLines(path, [UserLine]);
                await h.TickAsync();
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                File.AppendAllText(path, UserLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_working_clock_excludes_time_blocked_on_a_card() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false });
                h.Time.Advance(TimeSpan.FromSeconds(65));
                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                h.Time.Advance(TimeSpan.FromMinutes(10));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 07s");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// The daemon reports the parent waiting while its subagents still run; the note follows the
    /// count and clears when it drops to zero or is unknown.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_working_note_holds_while_only_subagents_run() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                var waiting = Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = true };
                await h.PushAsync(waiting with { LiveSubagents = 2 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");
                h.Time.Advance(TimeSpan.FromSeconds(65));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");

                await h.PushAsync(waiting with { LiveSubagents = 0 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                await h.PushAsync(waiting with { LiveSubagents = 1 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");

                await h.PushAsync(waiting with { LiveSubagents = null });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A card up means something is blocked on the user, and the note cannot know whether the
    /// asker is the parent or a subagent: the pause applies to background work too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_card_pauses_the_note_while_subagents_run() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = true, LiveSubagents = 1 });
                h.Time.Advance(TimeSpan.FromSeconds(65));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");

                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                h.Time.Advance(TimeSpan.FromMinutes(10));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 07s");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_lost_ack_keeps_input_unconfirmed_until_a_transcript_echo(bool newerSend) {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            using var presence = new System.Reactive.Subjects.BehaviorSubject<AgentPresence>(
                new AgentPresence(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" }, false));
            var ops = new ScriptedLocalControlOps();
            var input = new LocalFrameChatInput("a1", daemon, ops, presence);
            var h = new Harness(TranscriptChat.Journal, input: input);
            try {
                daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1"]));
                var path = Tmp.CreateFile("lost-ack.jsonl", []);
                await h.PushAsync(Hosted(path, "Running", false));
                ops.QueueSendText(new SendTextResult(false, SendTextReasons.Transport, "lost ack", null));
                h.Chat.ComposerText = "hello";
                await h.Chat.SendCommand.Execute();
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                await Assert.That(h.Chat.ComposerText).IsEqualTo("hello");
                if (newerSend) {
                    ops.QueueSendText(new SendTextResult(false, SendTextReasons.Transport, "another lost ack", null));
                    h.Chat.ComposerText = "later";
                    await h.Chat.SendCommand.Execute();
                }
                File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hello")) + "\n");
                await h.TickAsync();
                if (newerSend) {
                    await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
                    await Assert.That(h.Chat.ComposerHint).IsEqualTo("delivery unconfirmed — check the chat before sending again");
                    await Assert.That(h.Chat.ComposerText).IsEqualTo("later");
                    File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "later")) + "\n");
                    await h.TickAsync();
                }
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
                await Assert.That(h.Chat.ComposerHint).IsEqualTo("Enter sends · Shift+Enter for a new line");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("!kubectl get pods")]
    [Arguments("! kubectl get pods")]
    public async Task A_bang_command_echo_clears_the_queue_and_shows_the_command_and_output(string typed) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("bash.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = typed;
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                File.AppendAllText(path, """{"type":"user","message":{"content":"<bash-input>kubectl get pods</bash-input>"}}""" + "\n");
                File.AppendAllText(path, """{"type":"user","message":{"content":"<bash-stdout>web</bash-stdout><bash-stderr></bash-stderr>"}}""" + "\n");
                File.AppendAllText(path, NoteLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                var shell = h.Chat.Items.OfType<ShellCommandItem>().Single();
                await Assert.That(shell.Command).IsEqualTo("! kubectl get pods");
                await Assert.That(shell.Output).IsEqualTo("web");
                await Assert.That(h.Chat.Items.OfType<UserTurnItem>()).IsEmpty();
                await Assert.That(h.Chat.Items.OfType<SystemNoteItem>().Single().Text).IsEqualTo("**Agent finished**\n\nAll good.");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Bang_output_fills_the_command_row_when_it_arrives_later() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                var path = Tmp.CreateFile("bash-later.jsonl", ["""{"type":"user","message":{"content":"<bash-input>kubectl get pods</bash-input>"}}"""]);
                await h.PushAsync(Dto(path));
                var shell = h.Chat.Items.OfType<ShellCommandItem>().Single();
                await Assert.That(shell.HasOutput).IsFalse();
                File.AppendAllText(path, """{"type":"user","message":{"content":"<bash-stdout>web</bash-stdout>"}}""" + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.Items.OfType<ShellCommandItem>().Single()).IsSameReferenceAs(shell);
                await Assert.That(shell.Output).IsEqualTo("web");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_command_result_settles_one_send_without_a_chat_row() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                const string result = """{"type":"system","subtype":"local_command","commandRun":{"command":"rename","args":"receipt check"},"content":"<local-command-stdout>Session renamed to: receipt check</local-command-stdout>"}""";
                var path = Tmp.CreateFile("local-command.jsonl", [result]);
                await h.PushAsync(Dto(path));
                for (var i = 0; i < 2; i++) {
                    h.Chat.ComposerText = "/rename receipt check";
                    var send = h.Chat.SendCommand.Execute().ToTask();
                    input.Pending!.SetResult(ChatSendOutcome.Accepted);
                    await send;
                }
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(2);
                File.AppendAllText(path, """{"type":"system","subtype":"local_command","content":"<command-name>/rename</command-name><command-args>receipt check</command-args>"}""" + "\n");
                File.AppendAllText(path, result + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                await Assert.That(h.Chat.Items).IsEmpty();
                File.AppendAllText(path, result + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.Items).IsEmpty();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_slash_command_echo_acknowledges_input_without_a_display_row() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("slash.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "/clear";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                File.AppendAllText(path, """{"type":"user","message":{"content":"<command-name>/clear</command-name><local-command-stdout>ok</local-command-stdout>"}}""" + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.Items).IsEmpty();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Unconfirmed_send_and_echo_can_arrive_in_either_order_without_erasing_a_new_draft(bool echoFirst, bool editBack) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("uncertain.jsonl", []);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                h.Chat.ComposerText = "another draft";
                if (editBack) h.Chat.ComposerText = "hello";
                if (echoFirst) {
                    File.AppendAllText(path, UserLine + "\n");
                    await h.TickAsync();
                }
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await send;
                if (!echoFirst) {
                    await Assert.That(h.Chat.QueuedMessages.Single().IsUnconfirmed).IsTrue();
                    File.AppendAllText(path, UserLine + "\n");
                    await h.TickAsync();
                }
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo(editBack ? "hello" : "another draft");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(ChatSendOutcome.Accepted, false)]
    [Arguments(ChatSendOutcome.Rejected, false)]
    [Arguments(ChatSendOutcome.Unconfirmed, true)]
    public async Task Without_a_projection_only_uncertain_delivery_stays_in_the_queue(ChatSendOutcome outcome, bool queued) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(null, input: input);
            try {
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(outcome);
                await send;
                await Assert.That(h.Chat.HasQueuedMessages).IsEqualTo(queued);
                await Assert.That(h.Chat.ComposerText).IsEqualTo(outcome == ChatSendOutcome.Accepted ? "" : "hello");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Initial_history_and_preexisting_partial_lines_cannot_acknowledge_a_send(bool missing) {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = missing ? Tmp.PathTo("missing.jsonl") : Tmp.CreateFile("partial.jsonl", UserLine);
                await h.PushAsync(Dto(path));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                if (missing) File.WriteAllText(path, UserLine + "\n");
                else File.AppendAllText(path, "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.QueuedMessages).Count().IsEqualTo(1);
                File.AppendAllText(path, UserLine + "\n");
                await h.TickAsync();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Mixed_queue_distinguishes_unconfirmed_and_accepted_sends_and_marks_both_at_session_end() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            try {
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
                await send;
                h.Chat.ComposerText = "next";
                send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued · 1 unconfirmed");
                await Assert.That(h.Chat.QueuedMessages[0].IsUnconfirmed).IsTrue();
                await Assert.That(h.Chat.QueuedMessages[1].IsUnconfirmed).IsFalse();
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Completed" });
                await Assert.That(h.Chat.QueueSummary).IsEqualTo("2 messages unconfirmed");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A cache removal ends the session. The visible word is Done — the daemon has already
    /// dropped the agent — and a send that can never be echoed stops claiming it is queued.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_removed_agent_reads_as_done_and_unconfirms_the_queue() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.Journal, input: input);
            try {
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false));
                h.Chat.ComposerText = "hello";
                var send = h.Chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                await Assert.That(h.Chat.StatusText).IsEqualTo("Running");
                await Assert.That(h.Chat.QueuedMessages.Single().IsUnconfirmed).IsFalse();

                h.Daemon.Agents.Remove("a1");
                await Assert.That(h.Chat.StatusText).IsEqualTo("Done");
                await Assert.That(h.Chat.QueuedMessages.Single().IsUnconfirmed).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_usage_limit_question_is_shown_and_a_message_is_not_sent() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit · resets 3:10pm",
                "What do you want to do?", [
                    new(1, "Stop and wait for limit to reset"),
                    new(2, "Wait here, then continue automatically shortly"),
                    new(3, "Ask your admin for more usage"),
                ]);
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });
                h.Chat.ComposerText = "keep going";

                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(h.Chat.TerminalMenuChoices.Select(c => c.Label).ToArray()).IsEquivalentTo(new[] {
                    "Stop and wait for limit to reset",
                    "Wait here, then continue automatically shortly",
                    "Ask your admin for more usage",
                }, CollectionOrdering.Matching);
                await Assert.That(h.Chat.ComposerHint).Contains("usage limit");
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
                await h.Chat.TerminalMenuChoices[2].Choose.Execute().ToTask();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'3' }, CollectionOrdering.Matching);
                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });
                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(input.Keys.Count).IsEqualTo(1);

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                await Assert.That(h.Chat.HasTerminalMenu).IsFalse();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_usage_limit_choice_sends_one_digit_and_a_failed_send_can_be_retried() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit",
                "What do you want to do?", [
                    new(1, "Stop and wait for limit to reset"),
                    new(2, "Wait here, then continue automatically shortly"),
                ]);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            input.Gate = release.Task;
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });

                var running = h.Chat.TerminalMenuChoices[0].Choose.Execute().ToTask();
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsFalse();
                release.SetResult();
                await running;
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'1' }, CollectionOrdering.Matching);

                input.Accept = false;
                input.Gate = null;
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with {
                    Status = "Running",
                    UsageLimit = new UsageLimitNoticeDto(notice.Kind, "limit still held", notice.Prompt, notice.Options),
                });
                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await Assert.That(h.Chat.TerminalMenuError).Contains("not attached");
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsTrue();
                await Assert.That(input.Keys.Count).IsEqualTo(1);

                input.Accept = true;
                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'1', (byte)'2' }, CollectionOrdering.Matching);
                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_send_still_uploading_when_the_usage_limit_appears_is_not_pasted_into_the_menu() {
        await RunOnUiAsync(async () => {
            var input = new RecordingChatInput();
            var uploader = new HoldingUploader();
            var h = new Harness(TranscriptChat.For("claude"), input: input, uploader: uploader);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit",
                "What do you want to do?", [
                    new(1, "Stop and wait for limit to reset"),
                    new(2, "Wait here, then continue automatically shortly"),
                ]);
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                h.Chat.ComposerText = "keep going";
                h.Chat.Tray.AddAll([new StagedAttachment("note.txt", "text/plain", "hi"u8.ToArray())]);

                var sending = h.Chat.SendCommand.Execute().ToTask();
                await uploader.Started.Task;
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });
                uploader.Release.SetResult(new UploadOutcome(UploadKind.Uploaded, ["file-1"], null));
                await sending;

                await Assert.That(input.Sent).IsEmpty();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("keep going");
                await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins that the chat header names why an agent a daemon restart could not bring back stopped.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_interrupted_agent_shows_its_reason_in_the_chat_status() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
                await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.Resuming);

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted", Interruption = "worktree_missing" });

                await Assert.That(h.Chat.AgentStatus.Kind).IsEqualTo(AgentStatusKind.Interrupted);
                await Assert.That(h.Chat.AgentStatus.Tip).Contains("worktree_missing");
                await Assert.That(h.Chat.AgentStatus.Tip).Contains("kcap agent resume a1");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins a terminal select dialog as a chat question: text is held back, and a choice moves the
    /// dialog's cursor from where the screen shows it and confirms, in one write.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_dialog_is_answered_by_moving_its_cursor() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var dialog = new TerminalDialogDto("New MCP server found in this project: avalonia-docs",
                "MCP servers may execute code or access system resources.", [
                    "Use this MCP server",
                    "Use this and all future MCP servers in this project",
                    "Continue without using this MCP server",
                ], selected: 2, screen: "");
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });
                h.Chat.ComposerText = "hello";

                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(h.Chat.TerminalMenuTitle).IsEqualTo("Waiting for you in Terminal");
                await Assert.That(h.Chat.TerminalMenuHeading).IsEqualTo(dialog.Heading);
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();

                await h.Chat.TerminalMenuChoices[0].Choose.Execute().ToTask();
                await Assert.That(input.Writes).IsEquivalentTo(new[] { "\x1b[A\x1b[A\r" }, CollectionOrdering.Matching);

                var redrawn = new TerminalDialogDto(dialog.Heading, dialog.Body, dialog.Options, selected: 0, screen: "");
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = redrawn });
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsFalse();

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                await Assert.That(h.Chat.HasTerminalMenu).IsFalse();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins the fallback for a dialog whose choices could not be read: the card carries the
    /// screen text and sends the user to the terminal, and offers Escape to close it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_dialog_without_readable_choices_points_at_the_terminal() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            var shown = false;
            h.Chat.ShowTerminal = () => shown = true;
            var dialog = new TerminalDialogDto("", "", [], -1, "3 new MCP servers found in this project\n❯ [ ] docs\n  [ ] search");
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });

                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(h.Chat.TerminalMenuChoices.Single().Label).IsEqualTo("Close");
                await Assert.That(h.Chat.TerminalMenuScreen).IsEqualTo(dialog.Screen);
                await h.Chat.ShowTerminalCommand.Execute().ToTask();
                await Assert.That(shown).IsTrue();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments("/status", true, true)]
    [Arguments("/cost", true, true)]
    [Arguments("/status", null, false)]
    [Arguments("/status", false, false)]
    [Arguments("hello", true, false)]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_receipts_only_the_latest_idle_command(string text, bool? awaitingInput, bool retired) => RunOnUiAsync(async () => {
        var input = new KeyRecordingInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", AwaitingInput = awaitingInput };
            await h.PushAsync(agent);
            h.Chat.ComposerText = text;
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            await Assert.That(h.Chat.HasQueuedMessages).IsEqualTo(!retired);
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_receipt_can_arrive_before_transport_acceptance() => RunOnUiAsync(async () => {
        var input = new ScriptedInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", AwaitingInput = true };
            await h.PushAsync(agent);
            h.Chat.ComposerText = "/status";
            var send = h.Chat.SendCommand.Execute().ToTask();
            await WaitUntilAsync(() => input.Pending is not null, what: "the send awaiting acceptance");
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_in_another_transcript_does_not_receipt_an_earlier_command(bool arrivesWithSwitch) => RunOnUiAsync(async () => {
        var h = new Harness(TranscriptChat.For("claude"), input: new KeyRecordingInput());
        try {
            var first = Tmp.CreateFile("panel-first.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(first) with { AwaitingInput = true });
            h.Chat.ComposerText = "/status";
            await h.Chat.SendCommand.Execute().ToTask();
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
            var next = Tmp.CreateFile("panel-next.jsonl", [AssistantLine]);
            var agent = Dto(next) with { AwaitingInput = true };
            if (!arrivesWithSwitch) await h.PushAsync(agent);
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_panel_does_not_receipt_an_older_command_after_another_send() => RunOnUiAsync(async () => {
        var h = new Harness(TranscriptChat.For("claude"), input: new KeyRecordingInput());
        try {
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", AwaitingInput = true };
            await h.PushAsync(agent);
            foreach (var text in new[] { "/status", "hello" }) {
                h.Chat.ComposerText = text;
                await h.Chat.SendCommand.Execute().ToTask();
            }
            await h.PushAsync(agent with { TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310") });
            await Assert.That(h.Chat.QueuedMessages.Count).IsEqualTo(2);
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_screen_only_dialog_can_be_closed_once_with_escape() => RunOnUiAsync(async () => {
        var input = new KeyRecordingInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with {
                Status = "Running", TerminalDialog = new TerminalDialogDto("Status", "", [], -1, "Version: 2.1.310")
            });
            var close = h.Chat.TerminalMenuChoices.Single();
            await Assert.That(close.Label).IsEqualTo("Close");
            await close.Choose.Execute().ToTask();
            await Assert.That(input.Writes).IsEquivalentTo(new[] { "\x1b" }, CollectionOrdering.Matching);
            await Assert.That(await close.Choose.CanExecute.FirstAsync()).IsFalse();
        } finally { await h.TeardownAsync(); }
    });

    /// An empty chat can mean the agent is held at a prompt only its terminal shows, so it points
    /// there, unless the agent has no terminal or the dialog card already does.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_empty_chat_offers_the_terminal_until_the_dialog_card_does() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            try {
                await Assert.That(h.Chat.ShowsTerminalOffer).IsFalse();
                h.Chat.ShowTerminal = () => { };
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for chat…");
                await Assert.That(h.Chat.ShowsTerminalOffer).IsTrue();

                var dialog = new TerminalDialogDto("Do you want to proceed?", "", ["Yes", "No"], 0, "");
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });
                await Assert.That(h.Chat.ShowsTerminalMenu).IsTrue();
                await Assert.That(h.Chat.ShowsTerminalOffer).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    /// Pins the hook card's precedence: while a card asks the question, the screen's copy of the
    /// same dialog is not offered, and the composer stays shut because that dialog is still on
    /// the terminal. The card comes back once the hook card is gone and the dialog is not.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_card_hides_the_terminal_dialog_it_raised() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"), input: new KeyRecordingInput());
            var dialog = new TerminalDialogDto("Do you want to proceed?", "", ["Yes", "No"], 0, "");
            try {
                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                h.Chat.ComposerText = "hello";
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog });

                await Assert.That(h.Chat.ShowsTerminalMenu).IsFalse();
                await Assert.That(h.Chat.TerminalMenuChoices).IsEmpty();
                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();

                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ShowsTerminalMenu).IsTrue();
                await Assert.That(h.Chat.HasTerminalMenu).IsTrue();
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(h.Chat.TerminalMenuChoices).Count().IsEqualTo(2);
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_dialog_stays_answered_when_a_pending_card_hides_and_reveals_it() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var dialog = new TerminalDialogDto("Do you want to proceed?", "", ["Yes", "No"], 0, "");
            var agent = Agent("a1", "claude", hasTerminal: true) with { Status = "Running", TerminalDialog = dialog };
            try {
                await h.PushAsync(agent);
                await h.Chat.TerminalMenuChoices[0].Choose.Execute().ToTask();

                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards && !h.Chat.ShowsTerminalMenu, what: "the dialog hidden");
                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards && h.Chat.ShowsTerminalMenu, what: "the dialog revealed");

                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(input.Writes).IsEquivalentTo(new[] { "\r" }, CollectionOrdering.Matching);

                await h.PushAsync(agent with { TerminalDialog = null });
                await h.PushAsync(agent);
                await Assert.That(await h.Chat.TerminalMenuChoices[1].Choose.CanExecute.FirstAsync()).IsTrue();
                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await Assert.That(input.Writes).IsEquivalentTo(new[] { "\r", "\x1b[B\r" }, CollectionOrdering.Matching);
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A usage-limit menu is itself a select dialog, so the screen can report both. The dialog's
    /// cursor moving under an answered question must not offer the question's choices again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_answered_usage_limit_stays_answered_while_its_dialog_redraws() {
        await RunOnUiAsync(async () => {
            var input = new KeyRecordingInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            var notice = new UsageLimitNoticeDto(UsageLimitKinds.Blocked, "You've hit your session limit · resets 3:10pm",
                "What do you want to do?", [new(1, "Stop and wait for limit to reset"), new(2, "Ask your admin for more usage")]);
            static TerminalDialogDto DialogAt(int selected) =>
                new("What do you want to do?", "", ["Stop and wait for limit to reset", "Ask your admin for more usage"], selected, "");
            try {
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice, TerminalDialog = DialogAt(0) });
                await Assert.That(h.Chat.TerminalMenuPrompt).IsEqualTo(notice.Prompt);

                await h.Chat.TerminalMenuChoices[1].Choose.Execute().ToTask();
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice, TerminalDialog = DialogAt(1) });

                await Assert.That(await h.Chat.TerminalMenuChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'2' }, CollectionOrdering.Matching);
            } finally { await h.TeardownAsync(); }
        });
    }

    sealed class HoldingUploader : IAttachmentUploader {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UploadOutcome> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct) {
            Started.TrySetResult();
            return Release.Task;
        }
    }

    sealed class RecordingChatInput : AcceptingChatInput {
        public List<string> Sent { get; } = [];
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
            Sent.Add(text);
            return Task.FromResult(ChatSendOutcome.Accepted);
        }
    }

    sealed class KeyRecordingInput : AcceptingChatInput {
        public List<byte> Keys { get; } = [];
        public List<string> Writes { get; } = [];
        public bool Accept { get; set; } = true;
        public Task? Gate { get; set; }
        public override bool CanSendKeys => true;
        public override async Task<bool> SendKeysAsync(byte[] keys, CancellationToken ct) {
            if (Gate is { } gate) await gate;
            if (!Accept) return false;
            Keys.AddRange(keys);
            Writes.Add(System.Text.Encoding.ASCII.GetString(keys));
            return true;
        }
    }

    sealed class CountingProjection(ITranscriptProjection inner) : ITranscriptProjection {
        public List<int> LineNumbers { get; } = [];
        public int ContextsCreated { get; private set; }

        public TranscriptContext CreateContext(string sessionId, string? agentId) {
            ContextsCreated++;
            return inner.CreateContext(sessionId, agentId);
        }

        public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
            LineNumbers.Add(lineNumber);
            return inner.Project(line, lineNumber, receivedAt, context);
        }
    }

    /// A channel that takes every send, for the queue tests: the transcript, not the channel,
    /// is what retires a message.
    class AcceptingChatInput(string? senderUserId = "u2") : ChatInput {
        public override string? SenderUserId => senderUserId;
        public override SendAvailability Availability => SendAvailability.Ready;
        public override bool CanAcceptText => true;
        public override string Hint => "";
        public override bool CanAttach => true;
        public override string? AttachHint => null;
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) => Task.FromResult(ChatSendOutcome.Accepted);
        public override void Dispose() { }
    }

    sealed class AvailabilityInput : ChatInput {
        SendAvailability _availability = SendAvailability.Ready;

        public void SetAvailability(SendAvailability availability) {
            _availability = availability;
            this.RaisePropertyChanged(nameof(Availability));
            this.RaisePropertyChanged(nameof(CanAcceptText));
        }

        public override SendAvailability Availability => _availability;
        public override bool CanAcceptText => _availability == SendAvailability.Ready;
        public override string Hint => "";
        public override bool CanAttach => false;
        public override string? AttachHint => null;
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) =>
            Task.FromResult(ChatSendOutcome.Accepted);
        public override void Dispose() { }
    }

    sealed class EmptyFeed : IChatTranscriptFeed {
        public FeedRead ReadAppended() => new(FeedStatus.Ok, []);
        public long? CurrentOffset => 0;
        public void Dispose() { }
    }

    /// Serves one Reset read on demand — the pane opens its feed and reads once during
    /// construction, so a feed that resets on its first read would spend it before the test.
    sealed class ScriptedFeed : IChatTranscriptFeed {
        public bool ResetNext;
        public string? FailNext;

        public FeedRead ReadAppended() {
            if (FailNext is { } failure) {
                FailNext = null;
                return new(FeedStatus.Failed, [], Failure: failure);
            }
            if (!ResetNext) return new(FeedStatus.Ok, []);
            ResetNext = false;
            return new(FeedStatus.Reset, []);
        }

        public long? CurrentOffset => 0;
        public void Dispose() { }
    }

    static IObservable<ViewerIdentityDto?> Viewer(string? id) =>
        Observable.Return<ViewerIdentityDto?>(id is null ? null : new() { UserId = id });

    static QueuedInputItem Item(string text, Guid id, string? sender = "u2") => new() { DispatchId = id, Text = text, SenderUserId = sender };

    static ChatSessionInfo Session(string? feedKey) => new("Running", "Running", "gemini", null, null, null, false, "", feedKey);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_strip_shows_for_a_claude_session_with_an_account_and_not_from_an_older_daemon_or_another_vendor() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                await h.PushAsync(Dto(null) with { AccountDirectory = "/home/me/.claude-work", AccountId = "acc1", AccountLabel = "work" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountVendor).IsEqualTo("claude");
                await Assert.That(h.Chat.AccountLabel).IsEqualTo("work");
                await Assert.That(h.Chat.AccountDirectory).IsEqualTo("/home/me/.claude-work");

                await h.PushAsync(Dto(null, "codex") with { AccountDirectory = "/home/me/.codex-work", AccountId = "acc2", AccountLabel = "side" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountVendor).IsEqualTo("codex");

                await h.PushAsync(Dto(null));
                await Assert.That(h.Chat.ShowsAccountStrip).IsFalse();

                await h.PushAsync(Dto(null, "gemini") with { AccountDirectory = "/home/me/.gemini", AccountLabel = ".gemini" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unregistered_default_shows_its_directory_as_the_label() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            try {
                await h.PushAsync(Dto(null, "codex") with { AccountDirectory = "/home/me/.codex", AccountLabel = ".codex" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountLabel).IsEqualTo(".codex");
                await Assert.That(h.Chat.AccountDirectory).IsEqualTo("/home/me/.codex");

                await h.PushAsync(Dto(null, "codex") with { AccountDirectory = @"C:\Users\me\.codex\" });
                await Assert.That(h.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(h.Chat.AccountLabel).IsEqualTo(".codex");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_carries_the_account_onto_the_strip() {
        await RunOnUiAsync(async () => {
            var row = AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "codex",
                RegisteredAt = DateTime.UtcNow,
                AccountDirectory = "/Users/me/.codex-work", AccountId = "acc1", AccountLabel = "work",
            });
            using var session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(row, ended: false));
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                new AcceptingChatInput(), new NoAttachmentUploader(), null, new RecordingOpener(), time,
                new FakePermissionService(), new SessionRuns(time));
            try {
                await Assert.That(chat.ShowsAccountStrip).IsTrue();
                await Assert.That(chat.AccountVendor).IsEqualTo("codex");
                await Assert.That(chat.AccountLabel).IsEqualTo("work");
                await Assert.That(chat.AccountDirectory).IsEqualTo("/Users/me/.codex-work");

                session.OnNext(ChatSessionInfo.FromRemote(row with { AccountDirectory = null, AccountId = null, AccountLabel = null }, ended: false));
                await Assert.That(chat.ShowsAccountStrip).IsFalse();
            } finally { await chat.TeardownAsync(); }
        });
    }

    sealed class StripRig {
        public BehaviorSubject<AccountsSnapshotDto?> Readings { get; } = new(null);
        public BehaviorSubject<DateTimeOffset> Clock { get; } = new(StripReadings.Now);
        public Harness H { get; }

        public StripRig() => H = new Harness(TranscriptChat.For("claude"), accountReadings: Readings, usageClock: Clock);

        public Task PushAsync(string? accountId = "acc1") =>
            H.PushAsync(Dto(null) with { AccountDirectory = StripReadings.Directory, AccountId = accountId, AccountLabel = "work" });

        public ChatTabViewModel Chat => H.Chat;
    }

    static async Task SettleAsync() {
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_bars_for_a_drawable_reading() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(
                    StripReadings.Window(300, 42, TimeSpan.FromMinutes(134)), StripReadings.Window(10080, 81, TimeSpan.FromDays(3))))));
                await WaitUntilAsync(() => rig.Chat.ShowsAccountUsage, what: "the bars");

                var usage = rig.Chat.AccountStripUsage!;
                await Assert.That(usage.Bars.Select(b => (b.Label, b.PercentText, b.Tone)))
                    .IsEquivalentTo(new[] { ("5h", "42%", UsageTone.Neutral), ("Week", "81%", UsageTone.Warning) }, CollectionOrdering.Matching);
                await Assert.That(usage.NextResetText).IsEqualTo("5h resets in 2h 14m");
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsFalse();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_no_reading_yet_with_the_directory() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", null)));
                await WaitUntilAsync(() => rig.Chat.AccountReadingNote is not null, what: "the note");

                await Assert.That(rig.Chat.AccountReadingNote).IsEqualTo("· no reading yet");
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.ShowsAccountUsage).IsFalse();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_directory_only_when_the_host_is_not_drawable() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromHours(2))))));
                await WaitUntilAsync(() => rig.Chat.ShowsAccountUsage, what: "the bars");

                rig.Readings.OnNext(null);
                await WaitUntilAsync(() => !rig.Chat.ShowsAccountUsage, what: "the bars to go");

                await Assert.That(rig.Chat.AccountStripUsage).IsNull();
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_directory_only_for_an_unregistered_default() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromHours(2))))));
                await rig.PushAsync(accountId: null);
                await SettleAsync();

                await Assert.That(rig.Chat.ShowsAccountStrip).IsTrue();
                await Assert.That(rig.Chat.AccountStripUsage).IsNull();
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_shows_directory_only_for_an_account_missing_from_the_snapshot() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("other", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromHours(2))))));
                await rig.PushAsync();
                await SettleAsync();

                await Assert.That(rig.Chat.AccountStripUsage).IsNull();
                await Assert.That(rig.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(rig.Chat.AccountReadingNote).IsNull();
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_blocked_shows_limit_hit_with_its_reset() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Blocked(TimeSpan.FromMinutes(65),
                    StripReadings.Window(300, 100, TimeSpan.FromMinutes(65)), StripReadings.Window(10080, 81, TimeSpan.FromDays(3))))));
                await WaitUntilAsync(() => rig.Chat.ShowsAccountUsage, what: "the bars");

                var usage = rig.Chat.AccountStripUsage!;
                await Assert.That(usage.BlockedText).IsEqualTo("Limit hit · resets in 1h 05m");
                await Assert.That(usage.Bars[0].Tone).IsEqualTo(UsageTone.Danger);
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Strip_follows_the_clock() {
        await RunOnUiAsync(async () => {
            var rig = new StripRig();
            try {
                await rig.PushAsync();
                rig.Readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromMinutes(134).Add(TimeSpan.FromSeconds(30)))))));
                await WaitUntilAsync(() => rig.Chat.AccountStripUsage?.NextResetText == "5h resets in 2h 14m", what: "the first reset");
                var first = rig.Chat.AccountStripUsage;

                var changes = 0;
                using var watch = rig.Chat.WhenAnyValue(c => c.AccountStripUsage).Skip(1).Subscribe(_ => changes++);
                rig.Clock.OnNext(StripReadings.Now.AddSeconds(20));
                await SettleAsync();
                await Assert.That(changes).IsEqualTo(0);
                await Assert.That(rig.Chat.AccountStripUsage).IsSameReferenceAs(first);

                rig.Clock.OnNext(StripReadings.Now.AddMinutes(30));
                await WaitUntilAsync(() => rig.Chat.AccountStripUsage?.NextResetText == "5h resets in 1h 44m", what: "the clock's reset");
            } finally { await rig.H.TeardownAsync(); }
        });
    }

    /// Pins the remote card: the server relays the dialog a hosted agent shows, and a channel that
    /// cannot type into it shows the dialog as drawn and sends the viewer to the terminal.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_terminal_dialog_sends_the_viewer_to_the_terminal() {
        await RunOnUiAsync(async () => {
            var dto = new AgentInstanceDto {
                AgentId = "a1", Status = "running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "claude",
                RegisteredAt = DateTime.UtcNow,
                TerminalDialog = new AgentTerminalDialog {
                    Heading = "Allow external CLAUDE.md file imports?",
                    Options = ["No, disable external imports", "Yes, allow external imports"], Selected = 0,
                    Screen = "Allow external CLAUDE.md file imports?\n❯ No, disable external imports\n  Yes, allow external imports",
                },
            };
            using var session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(AgentRow.FromRemote(dto), ended: false));
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                new AcceptingChatInput(), new NoAttachmentUploader(), null, new RecordingOpener(), time,
                new FakePermissionService(), new SessionRuns(time));
            var shown = false;
            chat.ShowTerminal = () => shown = true;
            try {
                await Assert.That(chat.ShowsTerminalMenu).IsTrue();
                await Assert.That(chat.TerminalMenuTitle).IsEqualTo("Waiting for you in Terminal");
                await Assert.That(chat.TerminalMenuScreen).IsEqualTo(dto.TerminalDialog.Screen);
                await Assert.That(chat.TerminalMenuChoices).IsEmpty();
                await chat.ShowTerminalCommand.Execute().ToTask();
                await Assert.That(shown).IsTrue();

                session.OnNext(ChatSessionInfo.FromRemote(AgentRow.FromRemote(dto with { TerminalDialog = null }), ended: false));
                await Assert.That(chat.HasTerminalMenu).IsFalse();
            } finally { await chat.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Discard_removes_only_the_confirmed_attempt_and_preserves_failed_discards_and_the_draft() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            var first = Item("same text", Guid.NewGuid());
            var second = Item("same text", Guid.NewGuid());
            queue.OnNext([first, second]);
            chat.ComposerText = "keep my draft";
            var row = chat.QueuedMessages[0];
            await Assert.That(row.CanDiscard).IsTrue();
            var response = new TaskCompletionSource<HubCallOutcome>();
            lane.DiscardHandler = id => {
                if (id != first.DispatchId) throw new InvalidOperationException("wrong dispatch");
                return response.Task;
            };
            var discard = chat.DiscardQueuedCommand.Execute(row).ToTask();
            await WaitUntilAsync(() => !row.CanDiscard, what: "discard in flight");
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            response.SetResult(HubCallOutcome.Ok);
            await discard;
            await Assert.That(chat.QueuedMessages.Single().DispatchId).IsEqualTo(second.DispatchId);
            queue.OnNext([first, second]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(1);
            queue.OnNext([second]);
            // A subscription response can arrive after the newer removal push.
            queue.OnNext([first, second]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(1);

            lane.DiscardHandler = _ => Task.FromResult(HubCallOutcome.Failed("old server"));
            await chat.DiscardQueuedCommand.Execute(chat.QueuedMessages.Single());
            await Assert.That(chat.QueuedMessages.Single().DispatchId).IsEqualTo(second.DispatchId);
            await Assert.That(chat.ComposerText).IsEqualTo("keep my draft");
            await Assert.That(lane.UserInputs).IsEmpty();
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_held_send_is_discarded_through_the_server_and_an_unlisted_one_here() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            var discarded = new List<Guid>();
            var reply = new TaskCompletionSource<HubCallOutcome>();
            lane.DiscardHandler = id => { discarded.Add(id); return reply.Task; };

            chat.ComposerText = "held";
            await chat.SendCommand.Execute();
            chat.ComposerText = "dropped";
            await chat.SendCommand.Execute();
            var held = chat.QueuedMessages.Single(q => q.Text == "held");
            var dropped = chat.QueuedMessages.Single(q => q.Text == "dropped");
            var heldId = Guid.NewGuid();
            queue.OnNext([Item("held", heldId), Item("dropped", Guid.NewGuid())]);
            queue.OnNext([Item("held", heldId)]);
            await Assert.That(held.CanSendNow).IsFalse();
            await Assert.That(held.CanDiscard).IsTrue();
            await Assert.That(dropped.CanSendNow).IsTrue();

            var pending = chat.DiscardQueuedCommand.Execute(held).ToTask();
            await WaitUntilAsync(() => !held.CanDiscard, what: "discard in flight");
            await Assert.That(dropped.CanSendNow).IsFalse();
            reply.SetResult(HubCallOutcome.Ok);
            await pending;
            await Assert.That(discarded).IsEquivalentTo([heldId]);
            await Assert.That(dropped.CanSendNow).IsTrue();

            await chat.DiscardQueuedCommand.Execute(dropped);
            await Assert.That(chat.QueuedMessages).IsEmpty();
            await Assert.That(discarded).IsEquivalentTo([heldId]);
            await Assert.That(lane.UserInputs).IsEquivalentTo([("a1", "held"), ("a1", "dropped")]);
            await chat.TeardownAsync();
        });
    }

    /// Snapshots carry no revision and can land out of order: a listing after an unlisting makes
    /// the row held again, and nothing brings back a dispatch that was discarded, here or on the
    /// server, whatever session the pane moved through meanwhile.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_snapshot_cannot_bring_back_a_discarded_dispatch_or_leave_a_held_one_resendable() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            var serverDiscards = new List<Guid>();
            lane.DiscardHandler = id => { serverDiscards.Add(id); return Task.FromResult(HubCallOutcome.Ok); };

            chat.ComposerText = "mine";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            var ownId = Item("mine", Guid.NewGuid());
            queue.OnNext([ownId]);
            queue.OnNext([]);
            await Assert.That(own.CanSendNow).IsTrue();
            queue.OnNext([ownId]);
            await Assert.That(own.CanSendNow).IsFalse();
            await Assert.That(own.IsUnconfirmed).IsFalse();

            queue.OnNext([]);
            await chat.DiscardQueuedCommand.Execute(own);
            await Assert.That(serverDiscards).IsEmpty();
            queue.OnNext([ownId]);
            await Assert.That(chat.QueuedMessages).IsEmpty();

            var theirs = Item("theirs", Guid.NewGuid());
            queue.OnNext([theirs]);
            var reply = new TaskCompletionSource<HubCallOutcome>();
            lane.DiscardHandler = id => { serverDiscards.Add(id); return reply.Task; };
            var pending = chat.DiscardQueuedCommand.Execute(chat.QueuedMessages.Single()).ToTask();
            await WaitUntilAsync(() => serverDiscards.Count == 1, what: "the discard request");
            queue.OnNext([]);
            queue.OnNext([theirs]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(1);
            reply.SetResult(HubCallOutcome.Ok);
            await pending;
            await Assert.That(chat.QueuedMessages).IsEmpty();

            session.OnNext(Session("s2"));
            session.OnNext(Session("s1"));
            queue.OnNext([theirs, ownId]);
            await Assert.That(chat.QueuedMessages).IsEmpty();
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_remote_discard_waits_for_the_attempts_dispatch_identity() => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2")), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.CanDiscard).IsFalse();
            await chat.DiscardQueuedCommand.Execute(own);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);
            var item = Item("same prompt", Guid.NewGuid());
            queue.OnNext([item]);
            await Assert.That(own.CanDiscard).IsTrue();
            lane.DiscardHandler = id => {
                if (id != item.DispatchId) throw new InvalidOperationException("Wrong dispatch");
                return Task.FromResult(HubCallOutcome.Ok);
            };
            await chat.DiscardQueuedCommand.Execute(own);
            queue.OnNext([item]);
            await Assert.That(chat.QueuedMessages).IsEmpty();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task Same_text_from_another_sender_does_not_assign_their_dispatch_to_an_own_attempt(bool callerKnown) => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: callerKnown ? "u2" : null));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer(callerKnown ? "u2" : null)), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            var theirs = Item("same prompt", Guid.NewGuid(), "u1");
            var mine = Item("same prompt", Guid.NewGuid(), "u2");
            queue.OnNext([theirs, mine]);

            if (!callerKnown) {
                await Assert.That(own.DispatchId).IsNull();
                await Assert.That(own.CanDiscard).IsFalse();
                await Assert.That(chat.QueuedMessages.Count).IsEqualTo(3);
                return;
            }
            await Assert.That(own.DispatchId).IsEqualTo(mine.DispatchId);
            var discarded = new List<Guid>();
            lane.DiscardHandler = id => { discarded.Add(id); return Task.FromResult(HubCallOutcome.Ok); };
            await chat.DiscardQueuedCommand.Execute(own);
            await Assert.That(discarded).IsEquivalentTo([mine.DispatchId]);
            await Assert.That(chat.QueuedMessages.Single().DispatchId).IsEqualTo(theirs.DispatchId);
        } finally { await chat.TeardownAsync(); }
    });

    /// A failed discard says so; a later one that succeeds takes that hint away with the row.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_discard_that_succeeds_on_retry_clears_its_failure_hint() {
        await RunOnUiAsync(async () => {
            var lane = new FakeServerLane();
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
            var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            queue.OnNext([Item("old attempt", Guid.NewGuid())]);
            var row = chat.QueuedMessages.Single();

            lane.DiscardHandler = _ => Task.FromResult(HubCallOutcome.Failed("not yet"));
            await chat.DiscardQueuedCommand.Execute(row);
            await Assert.That(chat.ComposerHint).IsEqualTo("Could not discard this attempt. Try again.");

            lane.DiscardHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            await chat.DiscardQueuedCommand.Execute(row);
            await Assert.That(chat.QueuedMessages).IsEmpty();
            await Assert.That(chat.ComposerHint).IsNotEqualTo("Could not discard this attempt. Try again.");
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Discarding_a_retry_keeps_its_channel_warning_when_the_original_echoes() => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var path = Tmp.CreateFile("discard-retry-notice.jsonl", [AssistantLine]);
        var input = new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2"));
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), input,
            new NoAttachmentUploader(),
            _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var original = chat.QueuedMessages.Single();
            queue.OnNext([Item("same prompt", Guid.NewGuid())]);
            queue.OnNext([]);
            lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.Failed("reply lost"));
            await chat.SendQueuedCommand.Execute(original);
            var retry = chat.QueuedMessages.Single();
            queue.OnNext([Item("same prompt", Guid.NewGuid())]);
            queue.OnNext([]);
            var warning = input.Hint;
            await Assert.That(warning).Contains("delivery unconfirmed");
            chat.ComposerText = "keep my draft";
            await chat.DiscardQueuedCommand.Execute(retry);

            File.AppendAllText(path, User("same prompt") + "\n");
            await PollAsync(chat, time);
            await Assert.That(original.Acknowledged).IsTrue();
            await Assert.That(input.Hint).IsEqualTo(warning);
            await Assert.That(chat.ComposerText).IsEqualTo("keep my draft");
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Discarding_a_retry_preserves_the_recall_order_of_distinct_prompts() => RunOnUiAsync(async () => {
        var input = new ScriptedInput();
        var h = new Harness(TranscriptChat.For("claude"), input: input);
        try {
            var path = Tmp.CreateFile("discard-retry-recall.jsonl", [AssistantLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await SendAsync(h, input, "older prompt");
            var original = h.Chat.QueuedMessages.Single();
            var resend = h.Chat.SendQueuedCommand.Execute(original).ToTask();
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await resend;
            var retry = h.Chat.QueuedMessages.Single();
            await SendAsync(h, input, "newer prompt");
            await h.Chat.DiscardQueuedCommand.Execute(retry);
            h.Chat.ComposerText = "";

            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("newer prompt");
            await Assert.That(h.Chat.RecallOlder()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("older prompt");
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("newer prompt");
            await Assert.That(h.Chat.RecallNewer()).IsTrue();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
        } finally { await h.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task Discarding_a_retry_restores_its_original_before_later_matching_sends(bool laterSend) => RunOnUiAsync(async () => {
        var lane = new FakeServerLane();
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var path = Tmp.CreateFile("discard-retry.jsonl", [AssistantLine]);
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new ServerChatInput("a1", lane, access, session, hasTerminal: true, senderIdentity: Viewer("u2")),
            new NoAttachmentUploader(),
            _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var original = chat.QueuedMessages.Single();
            var first = Item("same prompt", Guid.NewGuid());
            queue.OnNext([first]);
            queue.OnNext([]);
            await chat.SendQueuedCommand.Execute(original);
            var retry = chat.QueuedMessages.Single();
            var second = Item("same prompt", Guid.NewGuid());
            queue.OnNext([second]);
            queue.OnNext([]);
            await Assert.That(retry.IsUnconfirmed).IsTrue();

            QueuedChatMessage? later = null;
            if (laterSend) {
                chat.ComposerText = "same prompt";
                await chat.SendCommand.Execute();
                later = chat.QueuedMessages.Single(q => !ReferenceEquals(q, retry));
                queue.OnNext([first]);
            }
            chat.ComposerText = "keep my draft";
            await chat.DiscardQueuedCommand.Execute(retry);
            await Assert.That(chat.ComposerText).IsEqualTo("keep my draft");

            if (later is null) {
                queue.OnNext([first, second]);
                await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(original);
                await Assert.That(original.IsForeign).IsFalse();
                await Assert.That(original.DispatchId).IsEqualTo(first.DispatchId);
            } else {
                File.AppendAllText(path, User("same prompt") + "\n");
                await PollAsync(chat, time);
                await Assert.That(original.Acknowledged).IsTrue();
                await Assert.That(later.Acknowledged).IsFalse();
                await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(later);
                queue.OnNext([second]);
            }
            await Assert.That(chat.QueuedMessages.Contains(retry)).IsFalse();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retry_listed_beside_its_original_hides_neither_attempt(bool listingBeforeAcceptance) {
        await RunOnUiAsync(async () => {
            using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var time = new FakeTimeProvider();
            var input = new ScriptedInput();
            var path = Tmp.CreateFile("retry-listing.jsonl", [AssistantLine]);
            var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                input, new NoAttachmentUploader(),
                _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
            try {
                await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
                chat.ComposerText = "do it";
                var send = chat.SendCommand.Execute().ToTask();
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                var original = chat.QueuedMessages.Single();
                await Assert.That(original.CanSendNow).IsTrue();

                var resend = chat.SendQueuedCommand.Execute(original).ToTask();
                var first = Item("do it", Guid.NewGuid()) with { DispatchedAt = DateTimeOffset.UnixEpoch };
                var second = Item("do it", Guid.NewGuid()) with { DispatchedAt = DateTimeOffset.UnixEpoch.AddSeconds(1) };
                if (listingBeforeAcceptance) queue.OnNext([second, first]);
                input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await resend;
                if (!listingBeforeAcceptance) queue.OnNext([second, first]);
                await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
                await Assert.That(original.DispatchId).IsEqualTo(first.DispatchId);
                await Assert.That(chat.QueuedMessages.Contains(original)).IsTrue();
                var retry = chat.QueuedMessages.Single(q => !ReferenceEquals(q, original));
                await Assert.That(retry.IsForeign).IsFalse();
                await Assert.That(retry.DispatchId).IsEqualTo(second.DispatchId);
                await Assert.That(original.CanSendNow || retry.CanSendNow).IsFalse();

                File.AppendAllText(path, User("do it") + "\n");
                await PollAsync(chat, time);
                queue.OnNext([second]);
                await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(retry);
                queue.OnNext([]);
                File.AppendAllText(path, User("do it") + "\n");
                await PollAsync(chat, time);
                await Assert.That(chat.HasQueuedMessages).IsFalse();
            } finally { await chat.TeardownAsync(); }
        });
    }

    sealed class SeededLocalFeed(IChatTranscriptFeed inner) : IChatTranscriptFeed {
        bool _seeded;
        public long? CurrentOffset => inner.CurrentOffset;
        public FeedRead ReadAppended() {
            var read = inner.ReadAppended();
            if (_seeded) return read;
            _seeded = true;
            return read with { Status = FeedStatus.Reset, SnapshotOffset = inner.CurrentOffset };
        }
        public void Dispose() => inner.Dispose();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_late_original_echo_does_not_retire_a_server_held_retry() => RunOnUiAsync(async () => {
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
        var time = new FakeTimeProvider();
        var path = Tmp.CreateFile("late-echo.jsonl", [AssistantLine]);
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
            new AcceptingChatInput(), new NoAttachmentUploader(),
            _ => new SeededLocalFeed(new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { })),
            new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
        try {
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");
            chat.ComposerText = "look";
            await chat.SendCommand.Execute();
            var original = chat.QueuedMessages.Single();
            queue.OnNext([Item("look", Guid.NewGuid())]);
            queue.OnNext([]);
            await chat.SendQueuedCommand.Execute(original);
            var retry = chat.QueuedMessages.Single();
            queue.OnNext([Item("look", Guid.NewGuid())]);
            File.AppendAllText(path, User("look") + "\n");
            await PollAsync(chat, time);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(retry);
            await Assert.That(retry.CanSendNow).IsFalse();
            queue.OnNext([]);
            File.AppendAllText(path, User("look") + "\n");
            await PollAsync(chat, time);
            await Assert.That(chat.HasQueuedMessages).IsFalse();
        } finally { await chat.TeardownAsync(); }
    });

    /// A local send nothing will ever echo — an interactive slash command — would otherwise sit in
    /// the strip for the life of the pane.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_send_with_no_receipt_can_be_discarded_and_keeps_the_draft() {
        await RunOnUiAsync(async () => {
            var input = new ScriptedInput();
            var h = new Harness(TranscriptChat.For("claude"), input: input);
            try {
                var path = Tmp.CreateFile("t.jsonl", [AssistantLine]);
                await h.PushAsync(Dto(path) with { AwaitingInput = true });
                await SendAsync(h, input, "/status");
                var row = h.Chat.QueuedMessages.Single();
                await Assert.That(row.PickupNote).IsEqualTo(QueuedChatMessage.IdlePickupNote);
                await Assert.That(row.CanDiscard).IsTrue();
                h.Chat.ComposerText = "next thought";

                await h.Chat.DiscardQueuedCommand.Execute(row);
                await Assert.That(h.Chat.QueuedMessages).IsEmpty();
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("next thought");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_servers_queue_confirms_an_own_send_and_lists_and_retires_the_others() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput("u1"), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.IsForeign).IsFalse();
            await Assert.That(own.IsAwaitingPickup).IsTrue();
            await Assert.That(own.CanSendNow).IsTrue();

            var mine = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            queue.OnNext([Item("do it", mine, sender: "u1"), Item("and this", theirs)]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            await Assert.That(own.IsUnconfirmed).IsFalse();
            await Assert.That(own.IsAwaitingPickup).IsTrue();
            await Assert.That(own.CanSendNow).IsFalse();
            var foreign = chat.QueuedMessages.Single(q => q.IsForeign);
            await Assert.That(foreign.Text).IsEqualTo("and this");
            await Assert.That(foreign.IsAwaitingPickup).IsFalse();
            await Assert.That(chat.QueueSummary).IsEqualTo("1 message queued · 1 unconfirmed");

            queue.OnNext([Item("do it", mine, sender: "u1")]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);

            // The own message leaves with the transcript's echo, never with the queue alone — but a
            // dispatch the server stopped listing was delivered or withdrawn, and only the echo can
            // say which, so the row stops promising a pickup and is no longer the server's to retire.
            queue.OnNext([]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);
            await Assert.That(own.IsAwaitingPickup).IsFalse();
            await Assert.That(own.IsUnconfirmed).IsTrue();
            await Assert.That(own.CanSendNow).IsTrue();
            await Assert.That(own.CanDiscard).IsTrue();
            await Assert.That(chat.QueueSummary).IsEqualTo("1 message unconfirmed");

            // An item the server sent no id for is unkeyed: nothing here can retire it later, so
            // it is neither shown nor allowed to match a send of this pane's own.
            queue.OnNext([Item("do it", Guid.Empty, sender: "u1"), Item("and this", theirs)]);
            await Assert.That(chat.QueuedMessages.Count(q => q.IsForeign)).IsEqualTo(1);
            await Assert.That(chat.QueuedMessages.Single(q => q.IsForeign).Text).IsEqualTo("and this");
            await chat.TeardownAsync();
        });
    }

    /// A foreign row answers for one session. The pane moving to another — or to none, where no
    /// snapshot can ever arrive to retire it — drops it, while this pane's own send rides along.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_change_drops_the_foreign_rows_and_keeps_an_own_send() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => new ScriptedFeed { ResetNext = true },
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionRuns(new FakeTimeProvider()), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            queue.OnNext([Item("and this", Guid.NewGuid())]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);

            session.OnNext(Session("s2"));
            await Assert.That(chat.QueuedMessages.Single().IsForeign).IsFalse();

            queue.OnNext([Item("and theirs again", Guid.NewGuid())]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            session.OnNext(Session(null));
            await Assert.That(chat.QueuedMessages.Single().IsForeign).IsFalse();
            await chat.TeardownAsync();
        });
    }

    /// A foreign row is the server's, not this pane's: neither an ended session nor a transcript
    /// reset may rebase one or cast doubt on a delivery this pane never made.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_foreign_row_is_never_rebased_nor_marked_unconfirmed() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feed = new ScriptedFeed { ResetNext = true };
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time), serverQueue: queue);
            await WaitUntilAsync(() => chat.HasSeed, what: "the seed");

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            queue.OnNext([Item("and this", Guid.NewGuid())]);
            var own = chat.QueuedMessages.Single(q => !q.IsForeign);
            var foreign = chat.QueuedMessages.Single(q => q.IsForeign);

            session.OnNext(Session("s1") with { Ended = true });
            await Assert.That(own.IsUnconfirmed).IsTrue();
            await Assert.That(foreign.IsUnconfirmed).IsFalse();

            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            feed.ResetNext = true;
            await PollAsync(chat, time);
            await Assert.That(chat.QueuedMessages.Single(q => q.IsForeign)).IsSameReferenceAs(foreign);
            await Assert.That(foreign.IsUnconfirmed).IsFalse();
            await chat.TeardownAsync();
        });
    }

    /// A refusal with nothing on screen replaces the wait with its reason; the next read that is
    /// not one clears it.
    /// A remote prompt sent before the transcript's seed has nothing to be acknowledged against: its
    /// echo can land inside the replayed history. Send waits for the seed; a local pane does not.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(AgentOrigin.Remote, false)]
    [Arguments(AgentOrigin.Local, true)]
    public async Task Send_waits_for_the_transcript_seed_on_the_remote_lane(AgentOrigin origin, bool beforeSeed) {
        await RunOnUiAsync(async () => {
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feed = new ScriptedFeed();
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", origin, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            chat.ComposerText = "do it";

            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsEqualTo(beforeSeed);

            feed.ResetNext = true;
            await PollAsync(chat, time);
            await Assert.That(await chat.SendCommand.CanExecute.FirstAsync()).IsTrue();
            await chat.TeardownAsync();
        });
    }

    /// A queued prompt's send-now waits for the new feed's seed too, after the pane moves to another session.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_queued_prompt_waits_for_a_new_feeds_seed_before_sending_now() {
        await RunOnUiAsync(async () => {
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feeds = new Dictionary<string, ScriptedFeed> { ["s1"] = new() { ResetNext = true }, ["s2"] = new() { ResetNext = true } };
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), key => feeds[key],
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            await WaitUntilAsync(() => chat.HasSeed, what: "the first feed's seed");
            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.CanSendNow).IsTrue();

            session.OnNext(Session("s2"));
            await Assert.That(chat.HasSeed).IsFalse();
            await Assert.That(own.CanSendNow).IsFalse();

            await WaitUntilAsync(() => chat.HasSeed, what: "the new feed's seed");
            await Assert.That(own.CanSendNow).IsTrue();
            await chat.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_read_says_why_in_place_of_the_wait_until_a_read_is_not_refused() {
        await RunOnUiAsync(async () => {
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feed = new ScriptedFeed { FailNext = "not signed in" };
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(chat.Phase).IsEqualTo(ChatTabPhase.Failed);
            await Assert.That(chat.PhaseNote).IsEqualTo("The transcript could not be read: not signed in");
            await Assert.That(chat.ActivityNote).IsEqualTo("");

            // How long a refusal stands is the feed's to say: a read that is not one clears it.
            await PollAsync(chat, time);
            await Assert.That(chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(chat.PhaseNote).IsEqualTo("Waiting for chat…");
            await chat.TeardownAsync();
        });
    }
}

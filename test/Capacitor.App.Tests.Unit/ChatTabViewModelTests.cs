using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
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
    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string AgentLaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string AgentFinishLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}}""";

    static AgentStatusDto Dto(string? transcriptPath, string vendor = "claude") =>
        Agent("a1", vendor, hasTerminal: true, repoPath: "/repo/x") with { TranscriptPath = transcriptPath };

    static ToolGroupItem Group(ChatTabViewModel chat, int index) => (ToolGroupItem)chat.Items[index];
    static PendingCardItem[] CardRows(ChatTabViewModel chat) => [.. chat.Items.OfType<PendingCardItem>()];

    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTerminalAttachClientFactory Factory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public SessionSubagents Subagents { get; }
        public PlanActivity Plan { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }

        public Harness(IChatTranscriptProjection? projection, Action<FakePermissionService>? seed = null,
                       ChatInput? input = null, string? unavailableNote = null) {
            seed?.Invoke(Permissions);
            Subagents = new SessionSubagents(Time);
            Terminal = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel(
                "a1", Daemon, input ?? new TerminalChatInput(Terminal, "a1", Daemon, new ScriptedLocalControlOps(), Observable.Never<AgentPresence>()), new NoAttachmentUploader(), projection, Opener, Time, Permissions, Subagents, unavailableNote, planActivity: Plan);
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

    static Harness Claude(Action<FakePermissionService>? seed = null) => new(TranscriptChat.For("claude"), seed);

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
    public async Task Waits_until_a_path_then_renders_the_initial_load_in_file_order() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Waiting);
            await Assert.That(h.Chat.PhaseNote).IsEqualTo("Waiting for the transcript…");

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

            File.AppendAllText(path, ThinkingLine + "\n" + ReadCallLine + "\n");
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
            await Assert.That(h.Chat.HasRunningSubagents).IsTrue();
            await Assert.That(h.Chat.RunningSubagent!.Name).IsEqualTo("Explore");
            await Assert.That(h.Chat.RunningSubagent!.StateText).StartsWith("running in background · ");
            await Assert.That(h.Chat.SubagentSummary).IsEmpty();
            await Assert.That(h.Subagents.Rows.Single().IsBackground).IsTrue();
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.HasRunningSubagents));
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.RunningSubagent));
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.SubagentSummary));

            File.AppendAllText(path, AgentCallLine.Replace("toolu_A", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunningSubagent).IsNull();
            await Assert.That(h.Chat.SubagentSummary).IsEqualTo("2 subagents running");

            File.AppendAllText(path, AgentFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunningSubagent!.StateText).StartsWith("running · ");
            await Assert.That(h.Chat.SubagentSummary).IsEmpty();
            await Assert.That(h.Chat.Items.OfType<SystemNoteItem>().Count()).IsEqualTo(1);

            File.AppendAllText(path, ToolResultLine.Replace("t1", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows.Select(r => r.State)).IsEquivalentTo(new[] { SubagentState.Done, SubagentState.Done }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    /// A live turn owns the footer: the activity note is showing, and a foreground launch is
    /// already a Task row. The note returns once the turn is over and the run is still going.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_strip_yields_to_the_activity_note_while_a_turn_is_live() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var raised = new List<string?>();
            h.Chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path) with { AwaitingInput = false });
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for");
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();

            raised.Clear();
            await h.PushAsync(Dto(path) with { AwaitingInput = true });
            await Assert.That(h.Chat.ActivityNote).IsEmpty();
            await Assert.That(h.Chat.HasRunningSubagents).IsTrue();
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.HasRunningSubagents));
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
            await Assert.That(h.Chat.HasRunningSubagents).IsTrue();

            File.WriteAllLines(path, [UserLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows).IsEmpty();

            var other = Tmp.CreateFile("o.jsonl", [AgentCallLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.HasRunningSubagents).IsTrue();
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows).IsEmpty();
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
            await Assert.That(h.Subagents.Rows).Count().IsEqualTo(1);
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows.Single().StateText).IsEqualTo("stopped");

            File.WriteAllLines(path, [AgentCallLine.Replace("Map desktop chat UI surfaces", "Map")]);
            await h.TickAsync();
            await Assert.That(h.Subagents.Rows.Single().Description).IsEqualTo("Map");
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
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

            await Assert.That(h.Subagents.Rows).IsEmpty();
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
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
        public TaskCompletionSource<ChatSendOutcome>? Pending;
        public int Disposals;
        public bool CanAttachValue = true;
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
            return Pending.Task.ContinueWith(t => { Pending = null; this.RaisePropertyChanged(nameof(CanAcceptText)); return t.Result; }, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
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
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0m 0s");

            File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi")) + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for ");

            // The awaiting-input flag hides it (the turn ended and the agent waits on the user)…
            await h.PushAsync(Hosted(path, "Running", awaitingInput: true));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            await h.PushAsync(Hosted(path, "Running", awaitingInput: false));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0m 0s");

            // Output does not end the timer; only a turn verdict does.
            File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "on it")) + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for ");
            await h.TeardownAsync();
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
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");
            await h.PushAsync(dto); // repeated snapshots must not restart the clock
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");
            await h.PushAsync(dto with { AwaitingInput = true });
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await h.PushAsync(dto);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0m 0s");
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
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message queued");
            input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;
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
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 7s");
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
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0m 0s");
                h.Time.Advance(TimeSpan.FromSeconds(65));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");

                await h.PushAsync(waiting with { LiveSubagents = 0 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                await h.PushAsync(waiting with { LiveSubagents = 1 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0m 0s");

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
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");

                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                h.Time.Advance(TimeSpan.FromMinutes(10));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 7s");
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

    /// A cache removal is the other way a session ends: the footer has to say so — the daemon's
    /// vocabulary has no word for an agent it has already dropped — and a send that can never be
    /// echoed must stop claiming it is still queued.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_removed_agent_reads_as_Completed_and_unconfirms_the_queue() {
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
                await Assert.That(h.Chat.StatusText).IsEqualTo("Completed");
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

                await Assert.That(h.Chat.HasUsageLimitQuestion).IsTrue();
                await Assert.That(h.Chat.UsageLimitChoices.Select(c => c.Label).ToArray()).IsEquivalentTo(new[] {
                    "Stop and wait for limit to reset",
                    "Wait here, then continue automatically shortly",
                    "Ask your admin for more usage",
                }, CollectionOrdering.Matching);
                await Assert.That(h.Chat.ComposerHint).Contains("usage limit");
                await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
                await h.Chat.UsageLimitChoices[2].Choose.Execute().ToTask();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'3' }, CollectionOrdering.Matching);
                await Assert.That(await h.Chat.UsageLimitChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running", UsageLimit = notice });
                await Assert.That(await h.Chat.UsageLimitChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(input.Keys.Count).IsEqualTo(1);

                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with { Status = "Running" });
                await Assert.That(h.Chat.HasUsageLimitQuestion).IsFalse();
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

                var running = h.Chat.UsageLimitChoices[0].Choose.Execute().ToTask();
                await Assert.That(await h.Chat.UsageLimitChoices[1].Choose.CanExecute.FirstAsync()).IsFalse();
                release.SetResult();
                await running;
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'1' }, CollectionOrdering.Matching);

                input.Accept = false;
                input.Gate = null;
                await h.PushAsync(Agent("a1", "claude", hasTerminal: true) with {
                    Status = "Running",
                    UsageLimit = new UsageLimitNoticeDto(notice.Kind, "limit still held", notice.Prompt, notice.Options),
                });
                await h.Chat.UsageLimitChoices[1].Choose.Execute().ToTask();
                await Assert.That(h.Chat.UsageLimitError).Contains("not attached");
                await Assert.That(await h.Chat.UsageLimitChoices[1].Choose.CanExecute.FirstAsync()).IsTrue();
                await Assert.That(input.Keys.Count).IsEqualTo(1);

                input.Accept = true;
                await h.Chat.UsageLimitChoices[1].Choose.Execute().ToTask();
                await Assert.That(input.Keys).IsEquivalentTo(new[] { (byte)'1', (byte)'2' }, CollectionOrdering.Matching);
                await Assert.That(await h.Chat.UsageLimitChoices[0].Choose.CanExecute.FirstAsync()).IsFalse();
            } finally { await h.TeardownAsync(); }
        });
    }

    sealed class KeyRecordingInput : AcceptingChatInput {
        public List<byte> Keys { get; } = [];
        public bool Accept { get; set; } = true;
        public Task? Gate { get; set; }
        public override async Task<bool> SendKeyAsync(byte key, CancellationToken ct) {
            if (Gate is { } gate) await gate;
            if (!Accept) return false;
            Keys.Add(key);
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
    class AcceptingChatInput : ChatInput {
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

    static QueuedInputItem Item(string text, Guid id, string? sender = "u2") => new() { DispatchId = id, Text = text, SenderUserId = sender };

    static ChatSessionInfo Session(string? feedKey) => new("Running", "Running", "gemini", null, null, null, false, "", feedKey);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_servers_queue_confirms_an_own_send_and_lists_and_retires_the_others() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => new EmptyFeed(),
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionSubagents(new FakeTimeProvider()), serverQueue: queue);

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.IsForeign).IsFalse();

            var mine = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            queue.OnNext([Item("do it", mine, sender: "u1"), Item("and this", theirs)]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            await Assert.That(own.IsUnconfirmed).IsFalse();
            var foreign = chat.QueuedMessages.Single(q => q.IsForeign);
            await Assert.That(foreign.Text).IsEqualTo("and this");
            await Assert.That(chat.QueueSummary).IsEqualTo("2 messages queued");

            queue.OnNext([Item("do it", mine, sender: "u1")]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);

            // The own message leaves with the transcript's echo, never with the queue alone.
            queue.OnNext([]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);

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
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => new EmptyFeed(),
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionSubagents(new FakeTimeProvider()), serverQueue: queue);

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
            var feed = new ScriptedFeed();
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionSubagents(time), serverQueue: queue);

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
            time.Advance(ChatTabViewModel.PollInterval);
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(chat.QueuedMessages.Single(q => q.IsForeign)).IsSameReferenceAs(foreign);
            await Assert.That(foreign.IsUnconfirmed).IsFalse();
            await chat.TeardownAsync();
        });
    }

    /// A refusal with nothing on screen replaces the wait with its reason; the next read that is
    /// not one clears it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_read_says_why_in_place_of_the_wait_until_a_read_is_not_refused() {
        await RunOnUiAsync(async () => {
            var session = new BehaviorSubject<ChatSessionInfo>(Session("s1"));
            var feed = new ScriptedFeed { FailNext = "not signed in" };
            var time = new FakeTimeProvider();
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), new NoAttachmentUploader(), _ => feed,
                new RecordingOpener(), time, new FakePermissionService(), new SessionSubagents(time));
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(chat.Phase).IsEqualTo(ChatTabPhase.Failed);
            await Assert.That(chat.PhaseNote).IsEqualTo("The transcript could not be read: not signed in");
            await Assert.That(chat.ActivityNote).IsEqualTo("");

            // How long a refusal stands is the feed's to say: a read that is not one clears it.
            time.Advance(ChatTabViewModel.PollInterval);
            await (chat.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(chat.PhaseNote).IsEqualTo("");
            await chat.TeardownAsync();
        });
    }
}

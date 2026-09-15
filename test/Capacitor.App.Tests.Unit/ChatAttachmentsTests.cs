using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI.Reactive;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The composer's attachment path: staging through the sink, the upload that must land before the
/// send, and which chips a delivery clears. Every test runs under RunOnUiAsync and carries
/// [NotInParallel("AvaloniaSession")], like every other VM suite touching the dispatcher.
public class ChatAttachmentsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string BareTurn = """{"type":"user","message":{"role":"user","content":"hi"}}""";
    const string TrailerTurn = """{"type":"user","message":{"role":"user","content":"hi\n\n[Attached files: .attached/x/a.png]"}}""";

    static StagedAttachment Chip(string name) => new(name, "image/png", new byte[] { 1, 2, 3 });

    static AgentStatusDto Dto(string path) =>
        Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/x") with { TranscriptPath = path };

    /// A scripted ChatInput for composer tests: SendAsync completes when the test says so.
    sealed class ScriptedInput : ChatInput {
        public TaskCompletionSource<ChatSendOutcome>? Pending;
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
        public override void Dispose() { }
    }

    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public ScriptedInput Input { get; } = new();
        public ScriptedUploader Uploader { get; } = new();
        public ChatTabViewModel Chat { get; }

        public Harness(IChatTranscriptProjection? projection) =>
            Chat = new ChatTabViewModel(
                "a1", Daemon, Input, Uploader, projection, new RecordingOpener(), Time, new FakePermissionService());

        public async Task PushAsync(AgentStatusDto dto) {
            Daemon.Agents.AddOrUpdate(dto);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TickAsync() {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        /// Stages the chips, types the text and starts the send, returning the command's task with
        /// the upload still in flight.
        public Task Begin(string text, params string[] names) {
            if (names.Length > 0) Chat.Tray.AddAll([.. names.Select(Chip)]);
            Chat.ComposerText = text;
            return Resend();
        }

        TaskCompletionSource<UploadOutcome> _armed = new();

        /// Suspends the upload so the in-flight window is observable, then starts the send.
        public Task Resend() {
            _armed = new TaskCompletionSource<UploadOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            Uploader.Pending = _armed;
            return Chat.SendCommand.Execute().ToTask();
        }

        public void Release(UploadOutcome outcome) => _armed.SetResult(outcome);

        public Task TeardownAsync() => Chat.TeardownAsync();
    }

    static Harness Hosted() => new(TranscriptChat.Journal);

    static async Task RunningAsync(Harness h) =>
        await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running" });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Send_with_attachments_uploads_first_then_passes_the_ids_and_clears_exactly_the_sent_chips() {
        await RunOnUiAsync(async () => {
            var h = Hosted();
            await RunningAsync(h);

            var send = h.Begin("hi", "a.png", "b.png");

            await Assert.That(h.Chat.Uploading).IsTrue();
            await Assert.That(h.Chat.ComposerHint).IsEqualTo("Uploading 2 files…");
            await Assert.That(await h.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(h.Input.Sends).IsEmpty();
            await Assert.That(h.Uploader.Calls.Single().Select(f => f.FileName))
                .IsEquivalentTo(new[] { "a.png", "b.png" }, CollectionOrdering.Matching);

            h.Release(new UploadOutcome(UploadKind.Uploaded, ["A", "B"], null));
            await WaitUntilAsync(() => h.Input.Sends.Count == 1, what: "the send that follows the upload");

            await Assert.That(h.Chat.Uploading).IsFalse();
            await Assert.That(h.Input.Sends.Single().Ids).IsEquivalentTo(new[] { "A", "B" }, CollectionOrdering.Matching);

            h.Chat.Tray.AddAll([Chip("c.png")]);
            h.Input.Pending!.SetResult(ChatSendOutcome.Accepted);
            await send;

            await Assert.That(h.Chat.Tray.Items.Select(f => f.FileName))
                .IsEquivalentTo(new[] { "c.png" }, CollectionOrdering.Matching);
            await Assert.That(h.Chat.ComposerText).IsEqualTo("");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Upload_failure_sends_nothing_and_keeps_text_and_chips() {
        await RunOnUiAsync(async () => {
            var h = Hosted();
            await RunningAsync(h);

            var send = h.Begin("hi", "a.png");
            h.Release(UploadOutcome.Unauthorized("not_signed_in"));
            await send;

            await Assert.That(h.Chat.ComposerHint).IsEqualTo("sign in to attach files");
            await Assert.That(h.Chat.Uploading).IsFalse();
            await Assert.That(h.Input.Sends).IsEmpty();
            await Assert.That(h.Chat.ComposerText).IsEqualTo("hi");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);

            send = h.Resend();
            h.Release(UploadOutcome.Rejected("x"));
            await send;

            await Assert.That(h.Chat.ComposerHint).IsEqualTo("x");
            await Assert.That(h.Input.Sends).IsEmpty();
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Can_attach_false_refuses_before_the_upload() {
        await RunOnUiAsync(async () => {
            var h = Hosted();
            h.Input.CanAttachValue = false;
            await RunningAsync(h);

            await h.Begin("hi", "a.png");

            await Assert.That(h.Uploader.Calls).IsEmpty();
            await Assert.That(h.Input.Sends).IsEmpty();
            await Assert.That(h.Chat.ComposerHint).IsEqualTo("attachments need the daemon updated");
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await Assert.That(h.Chat.ComposerText).IsEqualTo("hi");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rejected_and_unconfirmed_keep_everything() {
        await RunOnUiAsync(async () => {
            var h = Hosted();
            await RunningAsync(h);

            var send = h.Begin("hi", "a.png");
            h.Release(new UploadOutcome(UploadKind.Uploaded, ["A"], null));
            await WaitUntilAsync(() => h.Input.Sends.Count == 1, what: "the send that follows the upload");
            h.Input.Pending!.SetResult(ChatSendOutcome.Rejected);
            await send;

            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await Assert.That(h.Chat.ComposerText).IsEqualTo("hi");
            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();

            send = h.Resend();
            h.Release(new UploadOutcome(UploadKind.Uploaded, ["A"], null));
            await WaitUntilAsync(() => h.Input.Sends.Count == 2, what: "the second send");
            h.Input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
            await send;

            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);
            await Assert.That(h.Chat.ComposerText).IsEqualTo("hi");
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");
            await h.TeardownAsync();
        });
    }

    /// The daemon appends the trailer to the prompt it delivers, so the bare text is a different
    /// turn — replayed history, or another prompt that happens to read the same.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unconfirmed_send_is_cleared_by_a_trailer_turn_and_not_by_bare_text() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            var path = Tmp.CreateFile("trailer.jsonl", []);
            await h.PushAsync(Dto(path));

            var send = h.Begin("hi", "a.png");
            h.Release(new UploadOutcome(UploadKind.Uploaded, ["A"], null));
            await WaitUntilAsync(() => h.Input.Sends.Count == 1, what: "the send that follows the upload");
            h.Input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
            await send;
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");

            File.AppendAllText(path, BareTurn + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasQueuedMessages).IsTrue();
            await Assert.That(h.Chat.Tray.Count).IsEqualTo(1);

            h.Chat.Tray.AddAll([Chip("b.png")]);
            File.AppendAllText(path, TrailerTurn + "\n");
            await h.TickAsync();

            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await Assert.That(h.Chat.Tray.Items.Select(f => f.FileName))
                .IsEquivalentTo(new[] { "b.png" }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Text_only_send_is_still_confirmed_by_bare_text() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.For("claude"));
            var path = Tmp.CreateFile("bare.jsonl", []);
            await h.PushAsync(Dto(path));

            var send = h.Begin("hi");
            await WaitUntilAsync(() => h.Input.Sends.Count == 1, what: "the send");
            await Assert.That(h.Uploader.Calls).IsEmpty();
            h.Input.Pending!.SetResult(ChatSendOutcome.Unconfirmed);
            await send;
            await Assert.That(h.Chat.QueueSummary).IsEqualTo("1 message unconfirmed");

            File.AppendAllText(path, BareTurn + "\n");
            await h.TickAsync();

            await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
            await h.TeardownAsync();
        });
    }

    /// Whichever of the transport ack and the transcript echo lands first, the chips that went out
    /// are the ones removed — a second confirmation clears nothing twice.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Transcript_before_ack_and_ack_first_produce_the_same_tray() {
        await RunOnUiAsync(async () => {
            foreach (var transcriptFirst in new[] { false, true }) {
                var h = new Harness(TranscriptChat.For("claude"));
                var path = Tmp.CreateFile($"order-{transcriptFirst}.jsonl", []);
                await h.PushAsync(Dto(path));

                var send = h.Begin("hi", "a.png");
                h.Release(new UploadOutcome(UploadKind.Uploaded, ["A"], null));
                await WaitUntilAsync(() => h.Input.Sends.Count == 1, what: "the send that follows the upload");

                if (transcriptFirst) {
                    File.AppendAllText(path, TrailerTurn + "\n");
                    await h.TickAsync();
                }
                h.Input.Pending!.SetResult(ChatSendOutcome.Accepted);
                await send;
                if (!transcriptFirst) {
                    File.AppendAllText(path, TrailerTurn + "\n");
                    await h.TickAsync();
                }

                await Assert.That(h.Chat.Tray.Count).IsEqualTo(0);
                await Assert.That(h.Chat.HasQueuedMessages).IsFalse();
                await Assert.That(h.Chat.ComposerText).IsEqualTo("");
                await h.TeardownAsync();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sink_accept_stages_files_and_shows_one_refusal_line() {
        await RunOnUiAsync(async () => {
            var h = Hosted();
            await RunningAsync(h);

            h.Chat.Attachments.Accept(new IntakeResult(
                [Chip("a.png")], [new("Docs", "is a folder"), new("big.zip", "is over 10 MB")]));

            await Assert.That(h.Chat.Tray.Items.Select(f => f.FileName))
                .IsEquivalentTo(new[] { "a.png" }, CollectionOrdering.Matching);
            await Assert.That(h.Chat.ComposerHint).IsEqualTo("`Docs` is a folder; `big.zip` is over 10 MB");

            h.Chat.ComposerText = "typing";
            await Assert.That(h.Chat.ComposerHint).IsEqualTo("scripted");
            await h.TeardownAsync();
        });
    }

    /// The tray refuses each file past the cap on its own, but the composer has one line to say it
    /// in, so the cap is stated once and the names it dropped listed after it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Over_the_per_prompt_cap_is_one_line_naming_the_files_it_dropped() {
        await RunOnUiAsync(async () => {
            var h = Hosted();
            await RunningAsync(h);

            var full = Enumerable.Range(0, InputWire.MaxAttachmentsPerPrompt).Select(i => Chip($"f{i}.png")).ToList();
            h.Chat.Attachments.Accept(new IntakeResult(full, []));
            h.Chat.Attachments.Accept(new IntakeResult([Chip("c.png"), Chip("d.png")], []));

            await Assert.That(h.Chat.Tray.Count).IsEqualTo(InputWire.MaxAttachmentsPerPrompt);
            await Assert.That(h.Chat.ComposerHint).IsEqualTo(
                $"only {InputWire.MaxAttachmentsPerPrompt} files per message — `c.png`, `d.png` not added");
            await h.TeardownAsync();
        });
    }

    /// A handler-level failure has no file to name, so the backticked name is dropped rather than
    /// rendered empty.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_handler_failure_is_shown_without_a_file_name() {
        await RunOnUiAsync(async () => {
            var h = Hosted();
            await RunningAsync(h);

            h.Chat.Attachments.Accept(new IntakeResult([], [new("", "the clipboard could not be read")]));

            await Assert.That(h.Chat.ComposerHint).IsEqualTo("the clipboard could not be read");
            await h.TeardownAsync();
        });
    }
}

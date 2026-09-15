using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.RemoteFixtures;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The remote chat host over a scripted lane: the pane follows the lease, which follows the row's
/// session id, and the chat is shown only while the lease's verdict says the session is readable.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteSessionViewModelTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly SessionAccessService Access;
        public readonly FakePermissionService Permissions = new();
        public readonly FakeAgentDirectory Directory = new();
        public readonly AgentActionService Actions = NewActions();
        public readonly FakeTimeProvider Time = new();
        public SessionDetailFetch Detail = new(RemoteFixtures.Detail());

        public Harness() {
            Access = new SessionAccessService(Lane, Time);
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        }

        public static AgentRow Row(string id = "a1", string? sessionId = "s1", string status = "Running", string vendor = "gemini") =>
            AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = id, SessionId = sessionId, Status = status, DaemonName = "work-mac",
                Vendor = vendor, OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
            });

        public RemoteSessionViewModel Build(AgentRow row) {
            Directory.Rows.AddOrUpdate(row);
            return new RemoteSessionViewModel(row, Directory, Access, Permissions, Actions, Lane, (_, _) => Task.FromResult(Detail), new RecordingOpener(), Time, () => new FakeTerminalSurface());
        }

        /// One chat poll: the pane reads its feed on the timer this harness owns.
        public async Task TickAsync(RemoteSessionViewModel vm) {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (vm.Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        /// Polls the condition, ticking the chat between checks, since rows land on the poll.
        public async Task UntilAsync(RemoteSessionViewModel vm, Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
                await TickAsync(vm);
                await Task.Delay(10);
            }
        }

        public void Dispose() {
            Access.Dispose();
            Permissions.Dispose();
            Directory.Dispose();
        }
    }

    [Test]
    public async Task Opening_a_remote_row_establishes_access_and_shows_its_server_lane_cards() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(h.Lane.ChatSubscribes).Contains("s1");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await Assert.That(vm.AccessNote).IsEqualTo("");

            var card = PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", [], false), DateTimeOffset.UtcNow);
            card.AgentId = "a1";
            h.Permissions.Add(card);

            await WaitUntilAsync(() => vm.Cards.HasPendingCards, what: "the card");
            await Assert.That(vm.RepoLabelText).Contains("work-mac");
            await vm.TeardownAsync();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "released on teardown");
        });
    }

    [Test]
    public async Task A_revocation_hides_the_cards_and_a_reconnect_rechecks_access() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            h.Lane.SessionAccessChangedSubject.OnNext("s1");
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Denied, what: "denied");
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("You no longer have access to this session");

            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 2));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready after reconnect");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_row_without_a_session_waits_and_a_removed_row_ends_the_session() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(id: "a2", sessionId: null));
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.NoSession);
            await Assert.That(vm.AccessNote).IsEqualTo("Waiting for the session to start");
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(h.Lane.AccessWatches).IsEmpty();

            h.Directory.Rows.Remove("remote:a2");

            await Assert.That(vm.SessionEnded).IsTrue();
            await vm.TeardownAsync();
        });
    }

    /// A session id that only arrives with a later row revision still gets its lease, a change of
    /// id moves the lease with it, and a terminal status gives it back.
    [Test]
    public async Task The_lease_follows_the_rows_session_id_and_a_terminal_status_releases_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(sessionId: null));
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.NoSession);

            h.Directory.Rows.AddOrUpdate(Harness.Row(sessionId: "s2"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready on the late session id");

            h.Directory.Rows.AddOrUpdate(Harness.Row(sessionId: "s3"));
            await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s3"), what: "re-acquired on the new session id");
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s2"), what: "the old session released");

            h.Directory.Rows.AddOrUpdate(Harness.Row(sessionId: "s3", status: "Completed"));
            await Assert.That(vm.SessionEnded).IsTrue();
            // The transcript stays while the lease's last verdict stands: an ended session is still
            // readable, and giving the lease back is what stops new rows arriving.
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s3"), what: "released on the terminal status");
            await vm.TeardownAsync();
        });
    }

    /// A registry snapshot that briefly holds no rows removes this one; the refresh behind it
    /// re-adds the same live session, and the pane has to come back with it.
    [Test]
    public async Task A_row_that_comes_back_reopens_the_session_it_was_showing() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.Remove("remote:a1");
            await Assert.That(vm.SessionEnded).IsTrue();

            h.Directory.Rows.AddOrUpdate(Harness.Row());

            await Assert.That(vm.SessionEnded).IsFalse();
            await WaitUntilAsync(() => h.Lane.ChatSubscribes.Count == 2, what: "the lease re-acquired");
            await WaitUntilAsync(() => vm.ShowsChatPane, what: "the pane back");
            await vm.TeardownAsync();
        });
    }

    /// The local daemon proving the twin retires the remote row while the agent keeps running:
    /// nothing ended, and the window opens the local workspace for the same id instead.
    [Test]
    public async Task A_row_replaced_by_its_local_twin_reports_the_origin_change_rather_than_an_end() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.ProvenTwins.Add("a1");
            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.OriginChangedToLocal).IsTrue();
            await Assert.That(vm.SessionEnded).IsFalse();
            // Nothing is readable or stoppable through the released lease.
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo(RemoteSessionViewModel.OriginChangedNote);
            await Assert.That(await vm.StopCommand.CanExecute.FirstAsync()).IsFalse();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "the lease released");
            await vm.TeardownAsync();
        });
    }

    /// A same-id local row is not proof of a twin: the dedup fails open, so an unrelated local
    /// agent can carry this id. Calling that an origin change would tell the user the agent moved
    /// and point them at a process that has nothing to do with it.
    [Test]
    public async Task A_same_id_local_row_for_another_session_is_not_an_origin_change() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "a-different-session"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.OriginChangedToLocal).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("");
            await vm.TeardownAsync();
        });
    }

    /// A matching session id on a same-id local row is still not proof. The local daemon also has
    /// to be the twin of the daemon this agent is registered on, and the directory is the only
    /// thing that knows it — on another server the same session id names another session, and
    /// calling that an origin change points the user at a process that is not this agent.
    [Test]
    public async Task A_local_row_the_directory_does_not_call_a_twin_is_not_an_origin_change() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.OriginChangedToLocal).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("");
            await vm.TeardownAsync();
        });
    }

    /// The server is the winning authority while the local lane is down, and Completed is its
    /// verdict that the session ended. The retained local row reappearing as the remote one retires
    /// is history: reporting a move would point the user at a process that is not running this
    /// session any more.
    [Test]
    public async Task A_terminal_remote_status_ends_the_session_even_for_a_proven_twin() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.ProvenTwins.Add("a1");
            h.Directory.Rows.AddOrUpdate(Harness.Row(status: "Completed"));
            await Assert.That(vm.SessionEnded).IsTrue();

            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.OriginChangedToLocal).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("");
            await vm.TeardownAsync();
        });
    }

    /// A row that leaves the directory takes its hub subscriptions with it, whether or not the
    /// pane is torn down afterwards.
    [Test]
    public async Task A_removed_row_releases_the_lease_it_held() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "released on the removed row");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task The_chat_seeds_from_the_session_detail_and_follows_the_stream() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
                Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.Items.Count == 2, "the seeded rows");
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(vm.Chat.Items[0]).IsTypeOf<UserTurnItem>();
            await Assert.That(vm.Chat.Items[1]).IsTypeOf<AssistantTextItem>();

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantToolCallsGenerated, LsCall));
            await h.UntilAsync(vm, () => vm.Chat.Items.Count == 3, "the live row");
            await Assert.That(vm.Chat.Items[2]).IsTypeOf<ToolGroupItem>();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_sent_prompt_waits_in_the_queue_until_the_stream_echoes_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.Phase == ChatTabPhase.Reading, "the empty seed");
            await WaitUntilAsync(() => vm.Chat.ShowsComposer && vm.Chat.ComposerHint.StartsWith("Enter sends", StringComparison.Ordinal), what: "the composer");

            vm.Chat.ComposerText = "do it";
            await vm.Chat.SendCommand.Execute();
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "do it"));
            await Assert.That(vm.Chat.QueuedMessages.Count).IsEqualTo(1);
            await Assert.That(vm.Chat.ComposerText).IsEqualTo("");

            h.Lane.PushStreamEvent(Envelope("s1", 0, CanonicalEventTypes.UserMessageReceived, """{"content":"do it"}"""));
            await h.UntilAsync(vm, () => vm.Chat.QueuedMessages.Count == 0, "the echo");
            await Assert.That(vm.Chat.Items.Single()).IsTypeOf<UserTurnItem>();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_hidden_transcript_reads_as_not_available_and_a_lost_lane_keeps_the_rows() {
        await RunOnUiAsync(async () => {
            using var h = new Harness { Detail = new(null, NotFound: true) };
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.Phase == ChatTabPhase.Missing, "missing");
            await Assert.That(vm.Chat.PhaseNote).IsEqualTo(RemoteSessionViewModel.MissingNote);

            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Offline, what: "offline");
            // The banner has its own row above the panes: it reads over the retained rows rather
            // than replacing them, so a dropped lane does not blank the transcript.
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await Assert.That(vm.AccessNote).IsEqualTo("Not connected to the server");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_pty_harness_offers_a_terminal_tab_that_subscribes_once_access_stands_and_a_frame_harness_does_not() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var pty = h.Build(Harness.Row(vendor: "claude"));
            await Assert.That(pty.ShowsTerminalTab).IsTrue();
            await Assert.That(pty.IsChatActive).IsTrue();
            await WaitUntilAsync(() => pty.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => pty.Terminal!.Phase == RemoteTerminalPhase.Live, what: "the terminal live");
            await Assert.That(h.Lane.TerminalSubscribes).Contains("a1");

            await pty.ShowTerminalCommand.Execute();
            await Assert.That(pty.IsTerminalActive).IsTrue();
            await Assert.That(pty.ShowsTerminalPane).IsTrue();
            await Assert.That(pty.ShowsChatPane).IsFalse();
            await pty.TeardownAsync();
            await Assert.That(h.Lane.ResizeReleases).Contains("a1");

            var frame = h.Build(Harness.Row(id: "a2", sessionId: "s2", vendor: "gemini"));
            await Assert.That(frame.ShowsTerminalTab).IsFalse();
            await Assert.That(frame.Terminal).IsNull();
            await frame.TeardownAsync();
        });
    }

    [Test]
    public async Task A_prompt_queued_by_another_client_shows_in_the_chats_queue() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            h.Lane.PendingInputSubject.OnNext(new("s1", [new QueuedInputItem { DispatchId = Guid.NewGuid(), SenderUserId = "u2", Text = "after this one" }]));
            await WaitUntilAsync(() => vm.Chat.QueuedMessages.Count == 1, what: "the foreign row");
            await Assert.That(vm.Chat.QueuedMessages[0].IsForeign).IsTrue();
            h.Lane.PendingInputSubject.OnNext(new("other-session", []));
            await Assert.That(vm.Chat.QueuedMessages.Count).IsEqualTo(1);
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_lapsed_sign_in_says_so_in_place_of_the_transcript() {
        await RunOnUiAsync(async () => {
            using var h = new Harness { Detail = new(null, Unauthorized: true) };
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.Phase == ChatTabPhase.Failed, "the refusal");
            await Assert.That(vm.Chat.PhaseNote).IsEqualTo("The transcript could not be read: not signed in");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            // The stopped run answers every later poll with nothing; the explanation stays.
            await h.TickAsync(vm);
            await h.TickAsync(vm);
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Failed);
            await vm.TeardownAsync();
        });
    }

    /// The detail route answered but the stream refused: the seeded rows stay, and the reason sits
    /// under them instead of leaving a transcript that silently stops growing.
    [Test]
    public async Task A_refused_stream_keeps_the_seeded_rows_and_says_why_beneath_them() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
                Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
            h.Lane.TailHandler = (_, _) => new HubException(WireTokens.StreamNotAuthorized);
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.ActivityNote.Length > 0, "the reason");
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(vm.Chat.Items.Count).IsEqualTo(2);
            await Assert.That(vm.Chat.ActivityNote).IsEqualTo("The transcript could not be read: not authorized to read this session's stream");
            await vm.TeardownAsync();
        });
    }
}

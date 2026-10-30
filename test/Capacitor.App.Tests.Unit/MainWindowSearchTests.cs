using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class MainWindowSearchTests {
    static readonly PinPresentation NoPresentation = new(null, null, null);

    sealed class Harness : IDisposable {
        public RemoteHost Host { get; } = new();
        public FakeDaemonClientService Service { get; } = new();
        public HistoricalScheduler Scheduler { get; } = new();
        public List<string> Built { get; } = [];
        public List<SearchRequest> Requests { get; } = [];
        public System.Reactive.Subjects.Subject<string> AccessChanged { get; } = new();
        /// When set, a search answers once the test completes it.
        public TaskCompletionSource? SearchGate { get; set; }
        public CancellationToken LastSearchToken { get; private set; }
        public int Teardowns { get; private set; }
        public MainWindowViewModel Vm { get; }

        public Harness() {
            Vm = new MainWindowViewModel(
                Service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                trackWorkspaceTeardown: _ => Teardowns++,
                originOf: _ => AgentOrigin.Server,
                remoteWorkspaceFactory: id => { Built.Add(id); return Host.New(id, "S"); },
                directory: Host.Directory,
                serverWorkspaceFactory: id => { Built.Add(id); return Host.NewServer(id); },
                searchFactory: open => new SessionSearchViewModel(
                    async (request, ct) => {
                        Requests.Add(request);
                        LastSearchToken = ct;
                        if (SearchGate is { } gate) await gate.Task;
                        return new SearchFetch(ServerReadOutcome.Ok, new SearchResponseDto {
                            Sessions = [new() { SessionId = "T", Slug = "slug-t", Title = "Tee", StartedAt = "2026-10-29T09:00:00Z", MatchedField = "title", Snippet = "Tee" }],
                        });
                    },
                    Host.Sessions.Identity, AccessChanged, new FakeTimeProvider(), Scheduler, open),
                sessions: Host.Sessions);
        }

        public FakeServerSessionsService Sessions => Host.Sessions;

        public ISessionWorkspace OpenServer(string sessionId) {
            Host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow(sessionId));
            Vm.OpenSession(sessionId, AgentOrigin.Server);
            return Vm.CurrentWorkspace!;
        }

        public SearchHitViewModel TypeAndSettle(string query) {
            Vm.Search!.Query = query;
            Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(151));
            return Vm.Search.Hits[0];
        }

        public Task OpenSearch() => Vm.OpenSearchCommand.Execute().ToTask();

        /// The open session-only workspace's session is taken up by a daemon on another machine.
        public void HandOffToRemote(string agentId) =>
            Host.Directory.Rows.Edit(rows => {
                rows.RemoveKey("server:S");
                rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                    AgentId = agentId, SessionId = "S", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                    Vendor = "claude", RegisteredAt = DateTime.UtcNow,
                }));
            });

        public void ListServer(string sessionId) {
            Host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow(sessionId));
            Host.Directory.SessionRows[sessionId] = (sessionId, AgentOrigin.Server);
        }

        public void Dispose() {
            Vm.CloseWorkspace();
            Vm.Search?.Dispose();
            Host.Dispose();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_search_keeps_the_current_workspace_and_bumps_navigation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            var open = h.OpenServer("S");
            var generation = h.Vm.NavigationGeneration;

            await h.OpenSearch();

            await Assert.That(h.Vm.IsSearchOpen).IsTrue();
            await Assert.That(h.Vm.CurrentWorkspace).IsSameReferenceAs(open);
            await Assert.That(h.Vm.NavigationGeneration).IsGreaterThan(generation);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Escape_uncovers_the_workspace_without_teardown() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            var open = h.OpenServer("S");
            h.Sessions.PinnedIdSubject.OnNext("S");
            await h.OpenSearch();

            await h.Vm.DismissSearchCommand.Execute().ToTask();

            await Assert.That(h.Vm.IsSearchOpen).IsFalse();
            await Assert.That(h.Vm.CurrentWorkspace).IsSameReferenceAs(open);
            await Assert.That(h.Teardowns).IsEqualTo(0);
            await Assert.That(h.Sessions.UnpinCalls).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Covered_workspace_hand_off_does_not_dismiss_search() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            var open = h.OpenServer("S");
            h.Sessions.PinnedIdSubject.OnNext("S");
            await h.OpenSearch();

            h.HandOffToRemote("r1");

            await Assert.That(h.Vm.CurrentWorkspace).IsNotSameReferenceAs(open);
            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("r1");
            await Assert.That(h.Vm.IsSearchOpen).IsTrue();
            await Assert.That(h.Sessions.UnpinCalls).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Delayed_pin_still_opens_after_a_covered_hand_off() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.OpenServer("S");
            await h.OpenSearch();
            var gate = new TaskCompletionSource<PinOutcome>();
            h.Sessions.PinGate = gate;

            var opening = h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None);
            h.HandOffToRemote("r1");
            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("r1");
            h.ListServer("T");
            gate.SetResult(PinOutcome.Pinned);

            await Assert.That(await opening).IsEqualTo(SearchOpenOutcome.Opened);
            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("T");
            await Assert.That(h.Vm.IsSearchOpen).IsFalse();
            await Assert.That(h.Sessions.PinnedSessionId).IsEqualTo("T");
            await Assert.That(h.Sessions.UnpinCalls).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Delayed_pin_after_escape_opens_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            var open = h.OpenServer("S");
            await h.OpenSearch();
            var gate = new TaskCompletionSource<PinOutcome>();
            h.Sessions.PinGate = gate;

            var opening = h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None);
            await h.Vm.DismissSearchCommand.Execute().ToTask();
            h.ListServer("T");
            gate.SetResult(PinOutcome.Pinned);

            await Assert.That(await opening).IsEqualTo(SearchOpenOutcome.Superseded);
            await Assert.That(h.Vm.CurrentWorkspace).IsSameReferenceAs(open);
            await Assert.That(h.Built).IsEquivalentTo(["S"]);
            await Assert.That(h.Sessions.PinnedSessionId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pin_landing_after_escape_gives_the_selected_session_its_pin_back() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.ListServer("P");
            var owner = new PinPresentation("bob", "Bob", null);
            await Assert.That(await h.Vm.OpenSearchHitAsync("P", owner, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.Opened);
            h.Sessions.PinnedIdSubject.OnNext("P");
            await h.OpenSearch();
            var gate = new TaskCompletionSource<PinOutcome>();
            h.Sessions.PinGate = gate;

            var opening = h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None);
            await h.Vm.DismissSearchCommand.Execute().ToTask();
            h.Sessions.PinGate = null;
            gate.SetResult(PinOutcome.Pinned);

            await Assert.That(await opening).IsEqualTo(SearchOpenOutcome.Superseded);
            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("P");
            await Assert.That(h.Sessions.PinCalls[^1]).IsEqualTo(("P", owner));
            await Assert.That(h.Sessions.PinnedSessionId).IsEqualTo("P");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pin_that_lands_when_the_open_cannot_navigate_is_released() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            var gate = new TaskCompletionSource<PinOutcome>();
            h.Sessions.PinGate = gate;

            var opening = h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None);
            h.Vm.LatchShutdown();
            h.ListServer("T");
            gate.SetResult(PinOutcome.Pinned);

            await Assert.That(await opening).IsEqualTo(SearchOpenOutcome.Failed);
            await Assert.That(h.Sessions.PinnedSessionId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_listed_session_opened_from_search_asks_for_a_pin_and_a_scope_switch_does_not_unpin_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Sessions.Scope = RailScope.Team;
            h.ListServer("T");
            await h.OpenSearch();
            var teammate = new PinPresentation("mate", "Team Mate", null);

            var outcome = await h.Vm.OpenSearchHitAsync("T", teammate, CancellationToken.None);
            h.Sessions.Scope = RailScope.Mine;

            await Assert.That(outcome).IsEqualTo(SearchOpenOutcome.Opened);
            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("T");
            await Assert.That(h.Sessions.PinCalls).IsEquivalentTo([("T", teammate)]);
            await Assert.That(h.Sessions.PinnedSessionId).IsEqualTo("T");
            await Assert.That(h.Sessions.UnpinCalls).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rail_click_before_a_listed_hits_pin_lands_leaves_nothing_pinned() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.ListServer("T");
            await h.OpenSearch();
            var gate = new TaskCompletionSource<PinOutcome>();
            h.Sessions.PinGate = gate;

            await Assert.That(await h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.Opened);
            h.OpenServer("S");
            gate.SetResult(PinOutcome.Pinned);

            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("S");
            await Assert.That(h.Sessions.PinnedSessionId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rail_click_before_a_listed_hits_pin_lands_leaves_nothing_pinned_when_an_earlier_pin_was_held() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.ListServer("P");
            h.ListServer("T");
            await Assert.That(await h.Vm.OpenSearchHitAsync("P", NoPresentation, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.Opened);
            h.Sessions.PinnedIdSubject.OnNext("P");
            await h.OpenSearch();
            var gate = new TaskCompletionSource<PinOutcome>();
            h.Sessions.PinGate = gate;

            await Assert.That(await h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.Opened);
            h.OpenServer("S");
            gate.SetResult(PinOutcome.Pinned);

            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("S");
            await Assert.That(h.Sessions.PinnedSessionId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_click_dismisses_search_and_releases_the_pin() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.ListServer("P");
            await Assert.That(await h.Vm.OpenSearchHitAsync("P", NoPresentation, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.Opened);
            h.Sessions.PinnedIdSubject.OnNext("P");
            await h.OpenSearch();

            h.OpenServer("S");

            await Assert.That(h.Vm.IsSearchOpen).IsFalse();
            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("S");
            await Assert.That(h.Sessions.UnpinCalls).IsEqualTo(1);
            await Assert.That(h.Sessions.PinnedSessionId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rail_click_on_the_pinned_session_keeps_the_pin() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.OpenServer("S");
            h.Sessions.PinnedIdSubject.OnNext("S");
            await h.OpenSearch();

            h.Vm.OpenSession("S", AgentOrigin.Server);

            await Assert.That(h.Vm.IsSearchOpen).IsFalse();
            await Assert.That(h.Sessions.UnpinCalls).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task New_session_dismisses_search() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.OpenServer("S");
            h.Sessions.PinnedIdSubject.OnNext("S");
            await h.OpenSearch();

            await h.Vm.NewSessionCommand.Execute().ToTask();

            await Assert.That(h.Vm.IsSearchOpen).IsFalse();
            await Assert.That(h.Vm.CurrentWorkspace).IsNull();
            await Assert.That(h.Sessions.UnpinCalls).IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_the_already_open_session_closes_search_without_rebuild() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            var open = h.OpenServer("S");
            h.Host.Directory.SessionRows["S"] = ("S", AgentOrigin.Server);
            await h.OpenSearch();

            var outcome = await h.Vm.OpenSearchHitAsync("S", NoPresentation, CancellationToken.None);

            await Assert.That(outcome).IsEqualTo(SearchOpenOutcome.Opened);
            await Assert.That(h.Vm.IsSearchOpen).IsFalse();
            await Assert.That(h.Vm.CurrentWorkspace).IsSameReferenceAs(open);
            await Assert.That(h.Built).IsEquivalentTo(["S"]);
            await Assert.That(h.Teardowns).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Shortcut_while_open_requests_focus_without_reset() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            h.Vm.Search!.Query = "abc";
            h.Vm.Search.Tab = SearchMode.Transcripts;
            var focus = 0;
            using var _ = h.Vm.SearchFocusRequests.Subscribe(_ => focus++);
            var generation = h.Vm.NavigationGeneration;

            await h.OpenSearch();

            await Assert.That(focus).IsEqualTo(1);
            await Assert.That(h.Vm.IsSearchOpen).IsTrue();
            await Assert.That(h.Vm.Search.Query).IsEqualTo("abc");
            await Assert.That(h.Vm.Search.Tab).IsEqualTo(SearchMode.Transcripts);
            await Assert.That(h.Vm.NavigationGeneration).IsEqualTo(generation);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_search_resets_the_page() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            h.Vm.Search!.Query = "abc";
            await h.Vm.DismissSearchCommand.Execute().ToTask();

            await h.OpenSearch();

            await Assert.That(h.Vm.Search.Query).IsEqualTo("");
            await Assert.That(h.Vm.Search.Tab).IsEqualTo(SearchMode.Sessions);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Opening_search_invalidates_a_pending_launch_auto_open() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            h.Host.Directory.Rows.AddOrUpdate(RemoteHost.ServerRow("S"));
            var generation = h.Vm.NavigationGeneration;

            await h.OpenSearch();
            h.Vm.OpenSessionIfCurrent("S", AgentOrigin.Server, generation);

            await Assert.That(h.Vm.CurrentWorkspace).IsNull();
            await Assert.That(h.Vm.IsSearchOpen).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pin_failure_keeps_the_page_open_and_maps_the_outcome() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();

            h.Sessions.PinResult = PinOutcome.Unauthorized;
            await Assert.That(await h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.SignInRequired);
            h.Sessions.PinResult = PinOutcome.Unavailable;
            await Assert.That(await h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.Unavailable);
            h.Sessions.PinResult = PinOutcome.Failed;
            await Assert.That(await h.Vm.OpenSearchHitAsync("T", NoPresentation, CancellationToken.None)).IsEqualTo(SearchOpenOutcome.Failed);

            await Assert.That(h.Vm.IsSearchOpen).IsTrue();
            await Assert.That(h.Vm.CurrentWorkspace).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_hit_chosen_on_the_page_opens_and_closes_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            h.ListServer("T");
            var hit = h.TypeAndSettle("tee");

            await h.Vm.Search!.OpenAsync(hit);
            h.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(1));

            await Assert.That(h.Vm.CurrentWorkspace!.AgentId).IsEqualTo("T");
            await Assert.That(h.Vm.IsSearchOpen).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Escape_cancels_the_pages_pending_open() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            var hit = h.TypeAndSettle("tee");
            var gate = new TaskCompletionSource<PinOutcome>();
            h.Sessions.PinGate = gate;

            var opening = h.Vm.Search!.OpenAsync(hit);
            await h.Vm.DismissSearchCommand.Execute().ToTask();
            await h.OpenSearch();
            h.ListServer("T");
            gate.SetResult(PinOutcome.Pinned);
            await opening;
            h.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(1));

            await Assert.That(h.Vm.CurrentWorkspace).IsNull();
            await Assert.That(h.Vm.IsSearchOpen).IsTrue();
            await Assert.That(h.Sessions.PinnedSessionId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_closed_page_asks_nothing_on_an_access_change() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            h.TypeAndSettle("tee");
            await h.Vm.DismissSearchCommand.Execute().ToTask();
            var before = h.Requests.Count;

            h.AccessChanged.OnNext("T");
            h.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(151));

            await Assert.That(before).IsEqualTo(1);
            await Assert.That(h.Requests.Count).IsEqualTo(before);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_closed_page_asks_nothing_on_a_reconnect() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            h.TypeAndSettle("tee");
            await h.Vm.DismissSearchCommand.Execute().ToTask();
            var before = h.Requests.Count;

            h.Sessions.IdentitySubject.OnNext(new(0, 2, false));
            h.Sessions.IdentitySubject.OnNext(new(0, 3, true));
            h.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(151));

            await Assert.That(before).IsEqualTo(1);
            await Assert.That(h.Requests.Count).IsEqualTo(before);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Escape_cancels_a_search_still_in_flight() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            var gate = new TaskCompletionSource();
            h.SearchGate = gate;
            h.Vm.Search!.Query = "tee";
            h.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(151));
            var sent = h.Requests.Count;

            await h.Vm.DismissSearchCommand.Execute().ToTask();
            gate.SetResult();
            h.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(1));

            await Assert.That(sent).IsEqualTo(1);
            await Assert.That(h.LastSearchToken.IsCancellationRequested).IsTrue();
            await Assert.That(h.Vm.Search.Hits).IsEmpty();
            await Assert.That(h.Vm.Search.IsLoading).IsFalse();
        });
    }

    /// With no server to search, the cover still opens and closes, and the page is only the sign-in prompt.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_server_search_opens_the_cover_with_the_sign_in_prompt() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var vm = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System);
            var focus = 0;
            using var _ = vm.SearchFocusRequests.Subscribe(_ => focus++);

            await vm.OpenSearchCommand.Execute().ToTask();
            var opened = vm.IsSearchOpen;
            await vm.OpenSearchCommand.Execute().ToTask();
            await vm.DismissSearchCommand.Execute().ToTask();

            await Assert.That(vm.Search).IsNull();
            await Assert.That(opened).IsTrue();
            await Assert.That(focus).IsEqualTo(1);
            await Assert.That(vm.SearchShowsSignIn).IsTrue();
            await Assert.That(vm.IsSearchOpen).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_401_on_the_page_shows_the_sign_in_prompt() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness();
            await h.OpenSearch();
            var before = h.Vm.SearchShowsSignIn;
            var raised = 0;
            h.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainWindowViewModel.SearchShowsSignIn)) raised++; };

            h.Sessions.PinResult = PinOutcome.Unauthorized;
            var hit = h.TypeAndSettle("tee");
            await h.Vm.Search!.OpenAsync(hit);
            h.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(1));

            await Assert.That(before).IsFalse();
            await Assert.That(h.Vm.SearchShowsSignIn).IsTrue();
            await Assert.That(raised).IsGreaterThan(0);
        });
    }
}

using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class SessionSearchViewModelTests {
    sealed record Pending(SearchRequest Request, CancellationToken Token, TaskCompletionSource<SearchFetch> Completion);

    sealed class Harness : IDisposable {
        readonly List<Pending> _calls = [];
        public HistoricalScheduler Scheduler { get; } = new();
        public Subject<SessionIdentity> Identity { get; } = new();
        public Subject<string> Access { get; } = new();
        public SessionSearchViewModel Vm { get; }

        public Harness() {
            Vm = new SessionSearchViewModel(Search, Identity, Access, new FakeTimeProvider(), Scheduler);
            Identity.OnNext(new SessionIdentity(1, 1, true));
        }

        public IReadOnlyList<Pending> Calls { get { lock (_calls) return [.. _calls]; } }

        Task<SearchFetch> Search(SearchRequest request, CancellationToken token) {
            var pending = new Pending(request, token, new TaskCompletionSource<SearchFetch>());
            lock (_calls) _calls.Add(pending);
            return pending.Completion.Task;
        }

        public void Advance(int ms) => Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(ms));

        public Task Complete(int call, SearchFetch fetch) {
            Calls[call].Completion.SetResult(fetch);
            Advance(1);
            return Task.CompletedTask;
        }

        public void Dispose() => Vm.Dispose();
    }

    static SearchFetch Sessions(params string[] ids) => new(ServerReadOutcome.Ok, new SearchResponseDto {
        Sessions = [.. ids.Select(id => Hit(id))]
    });

    static SessionSearchHitDto Hit(string id, string matched = "title", string? endedAt = "2026-10-29T10:00:00Z") => new() {
        SessionId = id, Slug = "slug-" + id, Title = "T " + id, StartedAt = "2026-10-29T09:00:00Z",
        MatchedField = matched, Snippet = "T \u0002" + id + "\u0003", EndedAt = endedAt,
        RepoOwner = "acme", RepoName = "app", OwnerDisplayName = "Ada Lovelace", OwnerUser = "ada"
    };

    [Test]
    public async Task Waits_150ms_after_the_last_keystroke() {
        using var h = new Harness();

        h.Vm.Query = "loc";
        h.Advance(100);
        h.Vm.Query = "loca";
        h.Advance(100);
        await Assert.That(h.Calls).IsEmpty();
        h.Advance(50);

        await Assert.That(h.Calls.Count).IsEqualTo(1);
        await Assert.That(h.Calls[0].Request.Query).IsEqualTo("loca");
        await Assert.That(h.Calls[0].Request.Mode).IsEqualTo(SearchMode.Sessions);
    }

    [Test]
    public async Task Short_query_sends_nothing_and_cancels_the_in_flight_request() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        h.Vm.Query = "lo";
        h.Advance(500);

        await Assert.That(h.Calls.Count).IsEqualTo(1);
        await Assert.That(h.Calls[0].Token.IsCancellationRequested).IsTrue();
        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.TooShort);
        await Assert.That(h.Vm.ListMessage).IsEqualTo("Type at least 3 characters");
        await Assert.That(h.Vm.IsLoading).IsFalse();
    }

    [Test]
    public async Task Empty_query_shows_nothing() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);
        await h.Complete(0, Sessions("a"));

        h.Vm.Query = "  ";

        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.Empty);
        await Assert.That(h.Vm.ListMessage).IsNull();
        await Assert.That(h.Vm.Hits).IsEmpty();
    }

    [Test]
    public async Task Tab_switch_reruns_immediately_for_the_new_mode() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        h.Vm.NextTab();

        await Assert.That(h.Calls.Count).IsEqualTo(2);
        await Assert.That(h.Calls[1].Request).IsEqualTo(new SearchRequest("local", SearchMode.Transcripts));
        await Assert.That(h.Calls[0].Token.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task Tab_switch_with_a_short_query_sends_nothing() {
        using var h = new Harness();
        h.Vm.Query = "lo";

        h.Vm.NextTab();
        h.Vm.PreviousTab();

        await Assert.That(h.Calls).IsEmpty();
        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.TooShort);
    }

    [Test]
    public async Task Stale_response_for_an_older_query_is_dropped() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);
        h.Vm.Query = "locale";
        h.Advance(150);

        await h.Complete(0, Sessions("old"));
        await Assert.That(h.Vm.Hits).IsEmpty();

        await h.Complete(1, Sessions("new"));
        await Assert.That(h.Vm.Hits.Select(x => x.SessionId)).IsEquivalentTo(new[] { "new" });
        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.Results);
        await Assert.That(h.Vm.IsLoading).IsFalse();
    }

    [Test]
    public async Task Response_arriving_during_the_debounce_window_is_dropped() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        h.Vm.Query = "locale";
        await h.Complete(0, Sessions("old"));

        await Assert.That(h.Vm.Hits).IsEmpty();
    }

    [Test]
    public async Task Disconnect_drops_the_in_flight_response() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        h.Identity.OnNext(new SessionIdentity(1, 2, false));
        await h.Complete(0, Sessions("pre-disconnect"));

        await Assert.That(h.Calls[0].Token.IsCancellationRequested).IsTrue();
        await Assert.That(h.Vm.Hits).IsEmpty();
    }

    [Test]
    public async Task Stopword_query_answer_shows_no_results() {
        using var h = new Harness();
        h.Vm.Tab = SearchMode.Transcripts;
        h.Vm.Query = "the";
        h.Advance(150);

        await h.Complete(0, new SearchFetch(ServerReadOutcome.Ok, new SearchResponseDto { Sessions = [], Transcripts = null }));

        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.NoResults);
        await Assert.That(h.Vm.ListMessage).IsEqualTo("No transcript matches");
    }

    [Test]
    public async Task Empty_session_lane_shows_the_session_message() {
        using var h = new Harness();
        h.Vm.Query = "zzz";
        h.Advance(150);

        await h.Complete(0, Sessions());

        await Assert.That(h.Vm.ListMessage).IsEqualTo("No matching sessions");
    }

    [Test]
    public async Task Unauthorized_shows_the_sign_in_state() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        await h.Complete(0, new SearchFetch(ServerReadOutcome.Unauthorized, null));

        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.SignInRequired);
        await Assert.That(h.Vm.Hits).IsEmpty();
    }

    [Test]
    public async Task Failure_shows_the_error_line_and_keeps_no_hits() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);
        await h.Complete(0, Sessions("a"));
        h.Vm.Query = "locale";
        h.Advance(150);

        await h.Complete(1, new SearchFetch(ServerReadOutcome.Failed, null));

        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.Failed);
        await Assert.That(h.Vm.ListMessage).IsEqualTo("Search failed · try again");
        await Assert.That(h.Vm.Hits).IsEmpty();
    }

    [Test]
    public async Task Account_change_clears_and_drops_the_late_response() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        h.Identity.OnNext(new SessionIdentity(2, 2, true));
        await h.Complete(0, Sessions("late"));

        await Assert.That(h.Vm.Query).IsEqualTo("");
        await Assert.That(h.Vm.Hits).IsEmpty();
        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.Empty);
    }

    [Test]
    public async Task Reconnect_reruns_the_current_query() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);
        await h.Complete(0, Sessions("a"));

        h.Identity.OnNext(new SessionIdentity(1, 2, false));
        await Assert.That(h.Calls.Count).IsEqualTo(1);
        h.Identity.OnNext(new SessionIdentity(1, 3, true));

        await Assert.That(h.Calls.Count).IsEqualTo(2);
        await Assert.That(h.Calls[1].Request.Query).IsEqualTo("local");
    }

    [Test]
    public async Task Access_change_reruns_and_drops_the_pre_change_response() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        h.Access.OnNext("s1");
        await h.Complete(0, Sessions("pre-change"));

        await Assert.That(h.Calls.Count).IsEqualTo(2);
        await Assert.That(h.Calls[0].Token.IsCancellationRequested).IsTrue();
        await Assert.That(h.Vm.Hits).IsEmpty();

        await h.Complete(1, Sessions("post-change"));
        await Assert.That(h.Vm.Hits.Select(x => x.SessionId)).IsEquivalentTo(new[] { "post-change" });
    }

    [Test]
    public async Task Highlight_moves_and_clamps_and_resets_on_new_results() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);
        await h.Complete(0, Sessions("a", "b", "c"));

        h.Vm.MoveHighlight(-1);
        await Assert.That(h.Vm.HighlightIndex).IsEqualTo(0);
        h.Vm.MoveHighlight(5);
        await Assert.That(h.Vm.HighlightIndex).IsEqualTo(2);
        await Assert.That(h.Vm.Hits[2].IsHighlighted).IsTrue();
        await Assert.That(h.Vm.Hits[0].IsHighlighted).IsFalse();

        h.Vm.Query = "locale";
        h.Advance(150);
        await h.Complete(1, Sessions("d", "e"));

        await Assert.That(h.Vm.HighlightIndex).IsEqualTo(0);
        await Assert.That(h.Vm.Hits[0].IsHighlighted).IsTrue();
    }

    [Test]
    public async Task Session_caption_names_the_matched_field() {
        using var h = new Harness();
        h.Vm.Query = "local";
        h.Advance(150);

        await h.Complete(0, new SearchFetch(ServerReadOutcome.Ok, new SearchResponseDto {
            Sessions = [Hit("t"), Hit("s", "semantic"), Hit("b", "branch", endedAt: null)]
        }));

        await Assert.That(h.Vm.Hits[0].Caption).IsEqualTo("acme/app · Ada Lovelace");
        await Assert.That(h.Vm.Hits[1].Caption).IsEqualTo("acme/app · Ada Lovelace · matched by meaning");
        await Assert.That(h.Vm.Hits[2].Caption).IsEqualTo("acme/app · Ada Lovelace · matched on branch");
        await Assert.That(h.Vm.Hits[2].WhenText).IsEqualTo("Live");
        await Assert.That(h.Vm.Hits[0].OwnerInitials).IsEqualTo("AL");
        await Assert.That(h.Vm.Hits[0].Title.Any(r => r.Matched)).IsTrue();
    }

    [Test]
    public async Task Transcript_hit_shows_the_speaker_and_summary_for_turn_prose() {
        using var h = new Harness();
        h.Vm.Tab = SearchMode.Transcripts;
        h.Vm.Query = "local";
        h.Advance(150);

        await h.Complete(0, new SearchFetch(ServerReadOutcome.Ok, new SearchResponseDto {
            Transcripts = [
                new TranscriptSearchHitDto { SessionId = "a", Snippet = "x", Speaker = "assistant", Title = "Title" },
                new TranscriptSearchHitDto { SessionId = "b", Snippet = "y", Speaker = "assistant", Kind = "turn_prose" }
            ]
        }));

        await Assert.That(h.Vm.Hits[0].Speaker).IsEqualTo("assistant");
        await Assert.That(h.Vm.Hits[1].Speaker).IsEqualTo("summary");
        await Assert.That(h.Vm.Hits[0].WhenText).IsEqualTo("");
    }

    [Test]
    public async Task Reset_clears_the_page_and_cancels_in_flight_work() {
        using var h = new Harness();
        h.Vm.Tab = SearchMode.Transcripts;
        h.Vm.Query = "local";
        h.Advance(150);

        h.Vm.Reset();

        await Assert.That(h.Calls[0].Token.IsCancellationRequested).IsTrue();
        await Assert.That(h.Vm.Query).IsEqualTo("");
        await Assert.That(h.Vm.Tab).IsEqualTo(SearchMode.Sessions);
        await Assert.That(h.Vm.ListState).IsEqualTo(SearchListState.Empty);
    }
}

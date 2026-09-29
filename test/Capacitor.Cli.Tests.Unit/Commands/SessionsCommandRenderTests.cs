using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionsCommandRenderTests {
    const string Hash = "da9c523c68aee2f1";

    static readonly DateTimeOffset Started = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset Since   = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    static RepoSessionDto Row(
            string id, string status, string access, string? branch, bool stale = false, RepoSessionRepositoryDto? repo = null
        ) =>
        new(id, null, "Title " + id, new("github:1", "alice", "Alice", null), "claude", status, access, stale,
            Started, null, Started.AddHours(1), Hash, true, branch, "/w", null, [], 0, repo);

    static SessionsOptions Options(
            string          state    = "active",
            string?         repo     = null,
            bool            allRepos = false,
            DateTimeOffset? since    = null,
            string?         cursor   = null,
            int             limit    = 20
        ) =>
        new(state, repo, null, false, null, limit, false, allRepos, since, null, cursor);

    [Test]
    public async Task Table_shows_stale_in_place_of_active_and_blanks_branch_below_full() {
        var page = new RepoSessionsResponse([Row("s-1", "active", "full", "main", stale: true), Row("s-2", "active", "overview", null)], 2, 20, 0);

        var text = SessionsCommand.Render(page, "acme/widgets", Options());

        await Assert.That(text).Contains("SESSION");
        await Assert.That(text).Contains("s-1");
        await Assert.That(text).Contains("stale");
        await Assert.That(text).Contains("main");
        await Assert.That(text).Contains("overview");
        await Assert.That(text).Contains("kcap recap --full <session-id>");
        await Assert.That(text).DoesNotContain("STARTED");
        await Assert.That(text).DoesNotContain("REPO");

        var lines     = text.Split('\n');
        var branchCol = lines[0].IndexOf("BRANCH", StringComparison.Ordinal);
        var s2Line    = lines.Single(l => l.Contains("s-2"));

        await Assert.That(s2Line).DoesNotContain("main");
        await Assert.That(s2Line.Substring(branchCol, 24).Trim()).IsEmpty();
    }

    [Test]
    public async Task Table_shows_the_overflow_line_when_total_exceeds_the_page() {
        var page = new RepoSessionsResponse([Row("s-1", "active", "full", "main")], 5, 1, 0);

        var text = SessionsCommand.Render(page, "acme/widgets", Options());

        await Assert.That(text).Contains("Showing 1 of 5; raise --limit or narrow with --mine / --touching.");
    }

    [Test]
    public async Task Empty_page_says_so_with_the_state_and_repo() {
        var text = SessionsCommand.Render(new RepoSessionsResponse([], 0, 20, 0), "acme/widgets", Options("ended"));

        await Assert.That(text).Contains("No ended sessions visible to you on acme/widgets.");
    }

    [Test]
    public async Task Empty_page_for_every_state_names_no_state() {
        var text = SessionsCommand.Render(new RepoSessionsResponse([], 0, 20, 0), "acme/widgets", Options("all"));

        await Assert.That(text).Contains("No sessions visible to you on acme/widgets.");
    }

    [Test]
    public async Task Empty_period_says_so() {
        var text = SessionsCommand.Render(
            new RepoSessionsResponse([], 0, 20, 0, Since), "any repository", Options("all", "all", allRepos: true, since: Since));

        await Assert.That(text).Contains("No sessions visible to you on any repository in that period.");
    }

    [Test]
    public async Task A_period_listing_shows_the_start_and_offers_the_next_page() {
        var page = new RepoSessionsResponse([Row("s-1", "ended", "full", "main")], 5, 1, 0, Since, null, "eyJ2IjoxfQ");

        var text = SessionsCommand.Render(page, "acme/widgets", Options("all", "acme/widgets", since: Since, limit: 1));

        await Assert.That(text).Contains("STARTED");
        await Assert.That(text).Contains("Showing 1 of 5 in this period.");
        await Assert.That(text).DoesNotContain("raise --limit");
        await Assert.That(text).Contains("More: kcap sessions --repo acme/widgets --cursor eyJ2IjoxfQ --limit 1");
        await Assert.That(text.TrimEnd()).EndsWith("More: kcap sessions --repo acme/widgets --cursor eyJ2IjoxfQ --limit 1");
    }

    [Test]
    public async Task The_next_page_of_the_checkout_repo_names_it() {
        var page = new RepoSessionsResponse([Row("s-1", "ended", "full", "main")], 5, 1, 0, Since, null, "eyJ2IjoxfQ");

        var text = SessionsCommand.Render(page, "acme/widgets", Options("all", since: Since));

        await Assert.That(text).Contains("More: kcap sessions --repo acme/widgets --cursor eyJ2IjoxfQ --limit 20");
    }

    [Test]
    public async Task Listing_every_repository_shows_each_rows_repository() {
        var page = new RepoSessionsResponse(
            [
                Row("s-1", "ended", "full", "main", repo: new(Hash, "acme", "widgets")),
                Row("s-2", "ended", "full", "main", repo: new("0badf00d12345678", null, null)),
                Row("s-3", "ended", "full", "main")
            ], 3, 20, 0);

        var text = SessionsCommand.Render(page, "any repository", Options("all", "all", allRepos: true));

        var lines   = text.Split('\n');
        var repoCol = lines[0].IndexOf("REPO", StringComparison.Ordinal);

        await Assert.That(repoCol).IsGreaterThan(0);
        await Assert.That(lines.Single(l => l.Contains("s-1")).Substring(repoCol, 24).Trim()).IsEqualTo("acme/widgets");
        await Assert.That(lines.Single(l => l.Contains("s-2")).Substring(repoCol, 24).Trim()).IsEqualTo("0badf00d12345678");
        await Assert.That(lines.Single(l => l.Contains("s-3")).Substring(repoCol, 24).Trim()).IsEmpty();
    }

    [Test]
    public async Task A_page_with_a_cursor_and_no_rows_still_offers_the_next_page() {
        var page = new RepoSessionsResponse([], 5, 20, 0, Since, null, "bmV4dA");

        var text = SessionsCommand.Render(page, "any repository", Options("all", "all", allRepos: true, cursor: "cHJldg"));

        await Assert.That(text).Contains("No sessions on this page.");
        await Assert.That(text).Contains("More: kcap sessions --repo all --cursor bmV4dA --limit 20");
        await Assert.That(text).DoesNotContain("SESSION");
        await Assert.That(text).DoesNotContain("kcap recap --full");
    }

    [Test]
    public async Task Url_maps_mine_to_owner_me_and_encodes_touching() {
        var url = SessionsCommand.BuildUrl("http://srv", Hash, new("all", null, null, true, "src/Foo Bar", 7, false));

        await Assert.That(url).IsEqualTo($"http://srv/api/repositories/{Hash}/sessions?state=all&limit=7&owner=me&touching_path=src%2FFoo%20Bar");
    }

    [Test]
    public async Task Url_carries_the_period_as_utc_instants() {
        var options = new SessionsOptions("all", null, null, false, null, 20, false, false, Since, Since.AddDays(1));

        var url = SessionsCommand.BuildUrl("http://srv", Hash, options);

        await Assert.That(url).IsEqualTo(
            $"http://srv/api/repositories/{Hash}/sessions?state=all&limit=20&since=2026-09-14T00%3A00%3A00Z&until=2026-09-15T00%3A00%3A00Z");
    }

    [Test]
    public async Task Url_without_a_repository_is_the_listing_route() {
        var url = SessionsCommand.BuildUrl("http://srv", null, Options("all", "all", allRepos: true, since: Since));

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/listing?state=all&limit=20&since=2026-09-14T00%3A00%3A00Z");
    }

    [Test]
    public async Task Url_with_a_cursor_carries_only_the_cursor_and_the_limit() {
        var url = SessionsCommand.BuildUrl("http://srv", null, Options("all", "all", allRepos: true, cursor: "eyJ2IjoxfQ", limit: 50));

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/listing?cursor=eyJ2IjoxfQ&limit=50");
    }

    [Test]
    public async Task A_windowed_request_answered_without_the_window_was_ignored() {
        var unanswered = new RepoSessionsResponse([], 0, 20, 0);
        var answered   = new RepoSessionsResponse([], 0, 20, 0, null, Since);

        await Assert.That(SessionsCommand.IgnoredWindow(Options(since: Since), unanswered)).IsTrue();
        await Assert.That(SessionsCommand.IgnoredWindow(Options(cursor: "eyJ2IjoxfQ"), unanswered)).IsTrue();
        await Assert.That(SessionsCommand.IgnoredWindow(Options(since: Since), answered)).IsFalse();
        await Assert.That(SessionsCommand.IgnoredWindow(Options(), unanswered)).IsFalse();
    }
}

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

        var lines  = text.Split('\n');
        var s2Line = lines.Single(l => l.Contains("s-2"));

        await Assert.That(s2Line).DoesNotContain("main");
        await Assert.That(Cell(lines[0], s2Line, "BRANCH")).IsEmpty();
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

    /// <summary>The next-page line is pasted into a shell, so a value that is not a plain token is
    /// never printed into it, whichever side it came from.</summary>
    [Test]
    [Arguments("acme/widgets", "x; rm -rf ~")]
    [Arguments("acme;evil/widgets", "eyJ2IjoxfQ")]
    [Arguments("$(evil)/widgets", "eyJ2IjoxfQ")]
    public async Task A_value_that_is_not_a_token_is_not_printed_into_the_next_page_command(string repo, string cursor) {
        var page = new RepoSessionsResponse([Row("s-1", "ended", "full", "main")], 5, 1, 0, Since, null, cursor);

        var text = SessionsCommand.Render(page, repo, Options("all", repo, since: Since, limit: 1));

        await Assert.That(text).Contains("More: rows follow; read next_cursor with --json and pass it as --cursor.");
        await Assert.That(text).DoesNotContain("--cursor " + cursor);
        await Assert.That(text).DoesNotContain("--repo " + repo);
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

        var lines = text.Split('\n');

        await Assert.That(lines[0].IndexOf("REPO", StringComparison.Ordinal)).IsGreaterThan(0);
        await Assert.That(Cell(lines[0], lines.Single(l => l.Contains("s-1")), "REPO")).IsEqualTo("acme/widgets");
        await Assert.That(Cell(lines[0], lines.Single(l => l.Contains("s-2")), "REPO")).IsEqualTo("0badf00d12345678");
        await Assert.That(Cell(lines[0], lines.Single(l => l.Contains("s-3")), "REPO")).IsEmpty();
    }

    /// <summary>The text under <paramref name="column"/>, up to where the next header starts.</summary>
    static string Cell(string header, string line, string column) {
        var start = header.IndexOf(column, StringComparison.Ordinal);
        var end   = header.IndexOf(' ', start + column.Length);

        while (end >= 0 && end < header.Length && header[end] == ' ') end++;

        return line[start..Math.Min(end < 0 ? line.Length : end, line.Length)].Trim();
    }

    [Test]
    public async Task A_cut_cell_ends_in_an_ellipsis_the_output_can_carry() {
        var page = new RepoSessionsResponse([Row("s-1", "ended", "full", "capacitor/agent-c88662f0c1d2e3f4a5b6")], 1, 20, 0);

        var unicode = SessionsCommand.Render(page, "acme/widgets", Options("all"));
        var plain   = SessionsCommand.Render(page, "acme/widgets", Options("all"), encodable: false);

        await Assert.That(unicode).Contains("capacitor/agent-c88662f…");
        await Assert.That(plain).Contains("capacitor/agent-c8866...");
        await Assert.That(plain).DoesNotContain("…");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task No_row_is_wider_than_a_120_column_terminal(bool allRepos, bool windowed) {
        var row = Row("8c1f6e2a9b3d4c5e8f7a6b5c4d3e2f1a", "ended", "overview", "capacitor/agent-c88662f0c1d2e3f4",
            repo: new(Hash, "acme-testbench", "billing-service")) with {
            Title = "Investigate why the nightly billing export drops rows for accounts migrated last quarter"
        };
        var page = new RepoSessionsResponse([row], 1, 20, 0, windowed ? Since : null);

        var text = SessionsCommand.Render(
            page, "acme/widgets", Options("all", allRepos ? "all" : null, allRepos, windowed ? Since : null), width: 120);

        foreach (var line in text.Split('\n')) await Assert.That(line.TrimEnd('\r').Length).IsLessThan(120);
    }

    [Test]
    public async Task Redirected_output_keeps_the_whole_title() {
        var title = new string('t', 200);
        var page  = new RepoSessionsResponse([Row("s-1", "ended", "full", "main") with { Title = title }], 1, 20, 0);

        await Assert.That(SessionsCommand.Render(page, "acme/widgets", Options("all"))).Contains(title);
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

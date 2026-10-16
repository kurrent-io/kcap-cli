using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.PrDetection;

public class GitHubPrDetectorTests {
    [Test]
    public async Task Parses_gh_pr_view_json() {
        string? capturedCmd = null, capturedArgs = null;

        CommandRunner fake = (cmd, args, cwd, _) => {
            capturedCmd  = cmd;
            capturedArgs = args;
            return Task.FromResult<string?>(
                """{"number":12,"title":"Add thing","url":"https://github.com/o/r/pull/12","headRefName":"feat/x"}""");
        };

        var pr = await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake);

        await Assert.That(capturedCmd).IsEqualTo("gh");
        await Assert.That(capturedArgs).Contains("pr view");
        await Assert.That(pr!.Number).IsEqualTo(12);
        await Assert.That(pr.Title).IsEqualTo("Add thing");
        await Assert.That(pr.Url).IsEqualTo("https://github.com/o/r/pull/12");
        await Assert.That(pr.HeadRef).IsEqualTo("feat/x");
    }

    [Test]
    public async Task Null_output_yields_null() {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(null); // no PR / gh failed
        await Assert.That(await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake)).IsNull();
    }

    [Test]
    public async Task Malformed_json_yields_null() {
        // gh emitted non-JSON → JsonNode.Parse throws → best-effort catch returns null.
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("{not json");
        await Assert.That(await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake)).IsNull();
    }

    [Test]
    public async Task Non_numeric_number_yields_null() {
        // A non-integer `number` makes GetValue<int> throw → caught → null (never a bogus PrInfo).
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>("""{"number":"oops","title":"t"}""");
        await Assert.That(await GitHubPrDetector.DetectAsync("/cwd", TimeSpan.FromSeconds(2), fake)).IsNull();
    }

    static Task<PrInfo?> DetectForBranch(CommandRunner run, string host = "github.com", string owner = "acme",
                                         string repo = "widget", string branch = "remote-name") =>
        GitHubPrDetector.DetectForBranchAsync(host, owner, repo, branch, "/cwd", TimeSpan.FromSeconds(2), run);

    [Test]
    public async Task Branch_lookup_pins_the_repository_and_the_head() {
        string? capturedArgs = null;

        CommandRunner fake = (_, args, _, _) => {
            capturedArgs = args;
            return Task.FromResult<string?>(
                """{"number":7,"title":"t","url":"u","headRefName":"remote-name","isCrossRepository":false}""");
        };

        var pr = await DetectForBranch(fake);

        await Assert.That(capturedArgs).IsEqualTo(
            "pr view remote-name --repo github.com/acme/widget --json number,title,url,headRefName,isCrossRepository");
        await Assert.That(pr!.Number).IsEqualTo(7);
        await Assert.That(pr.HeadRef).IsEqualTo("remote-name");
    }

    [Test]
    [Arguments("""{"number":7,"headRefName":"remote-name","isCrossRepository":true}""")]
    [Arguments("""{"number":7,"headRefName":"other","isCrossRepository":false}""")]
    [Arguments("""{"number":7,"headRefName":"remote-name"}""")]
    public async Task Branch_lookup_rejects_a_head_that_is_not_the_repositorys_own_branch(string json) {
        CommandRunner fake = (_, _, _, _) => Task.FromResult<string?>(json);
        await Assert.That(await DetectForBranch(fake)).IsNull();
    }

    /// <summary>An organisation's <c>.github</c> repository is a legal name, and one argument is all
    /// a leading dot makes — so the lookup runs.</summary>
    [Test]
    public async Task Branch_lookup_runs_for_a_dot_prefixed_repository() {
        string? capturedArgs = null;

        CommandRunner fake = (_, args, _, _) => {
            capturedArgs = args;
            return Task.FromResult<string?>(
                """{"number":7,"headRefName":"remote-name","isCrossRepository":false}""");
        };

        var pr = await DetectForBranch(fake, repo: ".github");

        await Assert.That(capturedArgs).Contains("--repo github.com/acme/.github");
        await Assert.That(pr!.Number).IsEqualTo(7);
    }

    [Test]
    [Arguments("git hub.com", "acme", "widget", "remote-name")]
    [Arguments("github.com", "-acme", "widget", "remote-name")]
    [Arguments("github.com", "acme", "wid\"get", "remote-name")]
    [Arguments("github.com", "acme", "widget", "--web")]
    [Arguments("github.com", "acme", "widget", "123")] // gh would read this as PR #123
    public async Task Branch_lookup_never_spawns_gh_with_an_unsafe_argument(
            string host, string owner, string repo, string branch) {
        var spawned = false;

        CommandRunner fake = (_, _, _, _) => {
            spawned = true;
            return Task.FromResult<string?>(null);
        };

        await Assert.That(await DetectForBranch(fake, host, owner, repo, branch)).IsNull();
        await Assert.That(spawned).IsFalse();
    }

    const string History = "local1\nabc123\nolder\n";

    static CommandRunner HistoryRunner(string? prs, string? history = History, List<string>? calls = null) =>
        (cmd, args, _, _) => {
            calls?.Add($"{cmd} {args}");
            return Task.FromResult(cmd == "git" ? history : prs);
        };

    static Task<PrInfo?> DetectForHistory(CommandRunner run, string repo = "widget") =>
        GitHubPrDetector.DetectForHistoryAsync("github.com", "acme", repo, "/cwd", () => TimeSpan.FromSeconds(2), run);

    /// <summary>A branch pushed under another name with no upstream, and a local commit on top of
    /// the pushed head: neither name nor HEAD matches, the history does.</summary>
    [Test]
    public async Task History_lookup_finds_the_pr_whose_head_is_behind_local_commits() {
        var calls = new List<string>();
        var run = HistoryRunner(
            """[{"number":9,"title":"t","url":"u","headRefName":"fix/renamed","headRefOid":"ABC123","isCrossRepository":false}]""",
            calls: calls);

        var pr = await DetectForHistory(run);

        await Assert.That(pr!.Number).IsEqualTo(9);
        await Assert.That(pr.HeadRef).IsEqualTo("fix/renamed");
        await Assert.That(calls).Contains("git rev-list -n 100 HEAD --not --remotes=*/HEAD");
        await Assert.That(calls).Contains(
            "gh pr list --repo github.com/acme/widget --author @me --state open --limit 100 --json number,title,url,headRefName,headRefOid,isCrossRepository");
    }

    [Test]
    public async Task History_lookup_prefers_the_nearest_head() {
        var run = HistoryRunner("""
            [{"number":1,"headRefOid":"older","isCrossRepository":false},
             {"number":2,"headRefOid":"abc123","isCrossRepository":false}]
            """);

        await Assert.That((await DetectForHistory(run))!.Number).IsEqualTo(2);
    }

    [Test]
    [Arguments("""[{"number":9,"headRefOid":"abc123","isCrossRepository":true}]""")]
    [Arguments("""[{"number":9,"headRefOid":"abc123"}]""")]
    [Arguments("""[{"number":9,"headRefOid":"elsewhere","isCrossRepository":false}]""")]
    [Arguments("""[]""")]
    [Arguments("""{not json""")]
    [Arguments(null)]
    public async Task History_lookup_rejects_a_pr_not_on_this_history(string? prs) {
        await Assert.That(await DetectForHistory(HistoryRunner(prs))).IsNull();
    }

    /// <summary>The nearest commit heads two PRs: neither is linked, nor the older commit's PR.</summary>
    [Test]
    public async Task History_lookup_links_nothing_when_the_nearest_head_is_shared() {
        var run = HistoryRunner("""
            [{"number":1,"headRefOid":"older","isCrossRepository":false},
             {"number":2,"headRefOid":"abc123","isCrossRepository":false},
             {"number":3,"headRefOid":"abc123","isCrossRepository":false}]
            """);

        await Assert.That(await DetectForHistory(run)).IsNull();
    }

    [Test]
    public async Task History_lookup_needs_the_history() {
        var run = HistoryRunner("""[{"number":9,"headRefOid":"abc123","isCrossRepository":false}]""", history: null);
        await Assert.That(await DetectForHistory(run)).IsNull();
    }

    [Test]
    public async Task History_lookup_never_spawns_with_an_unsafe_argument() {
        var calls = new List<string>();
        await Assert.That(await DetectForHistory(HistoryRunner("[]", calls: calls), repo: "-widget")).IsNull();
        await Assert.That(calls).IsEmpty();
    }
}

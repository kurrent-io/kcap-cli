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

    [Test]
    [Arguments("git hub.com", "acme", "widget", "remote-name")]
    [Arguments("github.com", "-acme", "widget", "remote-name")]
    [Arguments("github.com", "acme", "wid\"get", "remote-name")]
    [Arguments("github.com", "acme", "widget", "--web")]
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
}

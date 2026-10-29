using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.RepoEvidence;

public class CreatedPullRequestsTests {
    static string ToolResult(string pr) =>
        """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"https://github.com/kurrent-io/kcap-cli/pull/1917"}]},"toolUseResult":{"stdout":"https://github.com/kurrent-io/kcap-cli/pull/1917","stderr":"","gitOperation":{"pr":"""
      + pr + "}}}";

    [Test]
    public async Task A_created_pr_is_read_from_the_tool_result_annotation() {
        var pr = CreatedPullRequests.FromClaudeLine(ToolResult("""{"number":1917,"url":"https://github.com/kurrent-io/kcap-cli/pull/1917","action":"created"}"""));

        await Assert.That(pr).IsEqualTo(new CreatedPullRequest("kurrent-io", "kcap-cli", 1917, "https://github.com/kurrent-io/kcap-cli/pull/1917"));
    }

    // Editing, commenting on or merging a PR says nothing about who opened it.
    [Test]
    [Arguments("edited")]
    [Arguments("commented")]
    [Arguments("merged")]
    public async Task Any_other_pr_action_links_nothing(string action) {
        var pr = CreatedPullRequests.FromClaudeLine(ToolResult($$"""{"number":1917,"url":"https://github.com/kurrent-io/kcap-cli/pull/1917","action":"{{action}}"}"""));

        await Assert.That(pr).IsNull();
    }

    [Test]
    public async Task A_number_that_disagrees_with_the_url_links_nothing() {
        var pr = CreatedPullRequests.FromClaudeLine(ToolResult("""{"number":1918,"url":"https://github.com/kurrent-io/kcap-cli/pull/1917","action":"created"}"""));

        await Assert.That(pr).IsNull();
    }

    // The server's link route records a github.com remote, whatever host the PR lives on.
    [Test]
    public async Task A_pr_on_another_host_links_nothing() {
        var pr = CreatedPullRequests.FromClaudeLine(ToolResult("""{"number":7,"url":"https://ghe.example.com/team/app/pull/7","action":"created"}"""));

        await Assert.That(pr).IsNull();
    }

    // A PR URL the agent merely printed is no evidence it opened that PR.
    [Test]
    public async Task A_pr_url_without_the_annotation_links_nothing() {
        const string line = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_1","type":"tool_result","content":"https://github.com/kurrent-io/kcap-cli/pull/1917"}]},"toolUseResult":{"stdout":"https://github.com/kurrent-io/kcap-cli/pull/1917"}}""";

        await Assert.That(CreatedPullRequests.FromClaudeLine(line)).IsNull();
    }

    [Test]
    public async Task A_malformed_line_links_nothing() {
        await Assert.That(CreatedPullRequests.FromClaudeLine("""{"gitOperation": """)).IsNull();
    }
}

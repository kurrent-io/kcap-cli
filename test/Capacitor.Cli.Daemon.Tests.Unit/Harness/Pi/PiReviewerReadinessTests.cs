using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiReviewerReadinessTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly IReadOnlyList<PiReviewerTool> Expected =
        [new("read_file", null, null), new("submit_review_result", "kcap-flow-result", "submit_review_result")];

    string ReadyFile(string? content) {
        var path = Tmp.PathTo("kcap-ready-" + Guid.NewGuid().ToString("N") + ".json");
        if (content is not null) File.WriteAllText(path, content);
        return path;
    }

    [Test]
    public async Task An_exact_match_in_any_order_is_ready() =>
        await Assert.That(PiReviewerReadiness.Verify(
            ReadyFile("""{"active":["submit_review_result","read_file"]}"""), Expected)).IsNull();

    [Test]
    [Arguments(null)]
    [Arguments("not json")]
    [Arguments("""{"active":"read_file"}""")]
    [Arguments("""{"active":["read_file"]}""")]
    [Arguments("""{"active":["read_file","submit_review_result","bash"]}""")]
    public async Task Anything_else_is_a_tool_surface_mismatch(string? content) =>
        await Assert.That(PiReviewerReadiness.Verify(ReadyFile(content), Expected))
            .StartsWith("pi_reviewer_tool_surface_mismatch:");
}

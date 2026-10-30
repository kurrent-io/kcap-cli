using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpServerInstructionsTests {
    /// <summary>Claude Code keeps the first 2,048 characters of a server's instructions and drops the rest.</summary>
    const int ClaudeCodeInstructionsCap = 2048;

    public static IEnumerable<Func<(string Server, string Instructions)>> AllInstructions() {
        yield return () => ("kcap-analytics", McpAnalyticsServer.ServerInstructions);
        yield return () => ("kcap-flows", McpFlowsServer.ServerInstructions);
        yield return () => ("kcap-pages", McpArtefactsServer.ServerInstructions);
        yield return () => ("kcap-handoff", McpHandoffServer.ServerInstructions);
        yield return () => ("kcap-knowledge", McpKnowledgeServer.ServerInstructions);
        yield return () => ("kcap-memory", McpMemoryServer.ServerInstructions);
        yield return () => ("kcap-plans", McpPlansServer.ServerInstructions);
        yield return () => ("kcap-review-context", McpReviewContextServer.Instructions);
        yield return () => ("kcap-review", McpReviewServer.ServerInstructions);
        yield return () => ("kcap-sessions", McpSessionsServer.ServerInstructions);
        yield return () => ("kcap-workitems", McpWorkItemsServer.ServerInstructions);
    }

    [Test]
    [MethodDataSource(nameof(AllInstructions))]
    public async Task Instructions_fit_in_what_Claude_Code_keeps(string server, string instructions) =>
        await Assert.That(instructions.Length).IsLessThanOrEqualTo(ClaudeCodeInstructionsCap).Because(server);
}

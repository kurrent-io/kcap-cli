using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Every kcap tool advertises MCP annotations. A tool without them is read by the spec's
/// defaults as destructive, open-world and not read-only, so a harness that decides approval from
/// annotations prompts for every call, pure reads included.</summary>
public class McpToolAnnotationsTests {
    static readonly (string Server, McpTool[] Tools)[] AllServers = [
        ("kcap-review",         McpReviewServer.BuildToolsList()),
        ("kcap-sessions",       McpSessionsServer.BuildToolsList()),
        ("kcap-analytics",      McpAnalyticsServer.BuildToolsList()),
        ("kcap-memory",         McpMemoryServer.BuildToolsList()),
        ("kcap-workitems",      McpWorkItemsServer.BuildToolsList()),
        ("kcap-plans",          McpPlansServer.BuildToolsList()),
        ("kcap-artefacts",      McpArtefactsServer.BuildToolsList()),
        ("kcap-flows",          McpFlowsServer.BuildToolsList()),
        ("kcap-flow-result",    McpFlowResultServer.BuildToolsList()),
        ("kcap-judge",          McpJudgeServer.BuildToolsList()),
        ("kcap-review-context", McpReviewContextServer.BuildToolsList())
    ];

    static McpTool Tool(string server, string name) =>
        AllServers.Single(s => s.Server == server).Tools.Single(t => t.Name == name);

    [Test]
    public async Task Every_tool_of_every_server_says_whether_it_reads_or_writes() {
        foreach (var (server, tools) in AllServers)
        foreach (var tool in tools)
            await Assert.That(tool.Annotations.ReadOnlyHint).IsNotNull()
                .Because($"{server}.{tool.Name} must advertise readOnlyHint");
    }

    [Test]
    public async Task A_write_says_whether_it_is_destructive_and_idempotent() {
        foreach (var (server, tools) in AllServers)
        foreach (var tool in tools.Where(t => t.Annotations.ReadOnlyHint is false)) {
            await Assert.That(tool.Annotations.DestructiveHint).IsNotNull().Because($"{server}.{tool.Name}");
            await Assert.That(tool.Annotations.IdempotentHint).IsNotNull().Because($"{server}.{tool.Name}");
            await Assert.That(tool.Annotations.OpenWorldHint).IsNotNull().Because($"{server}.{tool.Name}");
        }
    }

    [Test]
    public async Task Reads_are_read_only_and_writes_say_what_they_do() {
        await Assert.That(Tool("kcap-plans", "get_plan").Annotations.ReadOnlyHint).IsTrue();
        await Assert.That(Tool("kcap-memory", "search_memories").Annotations.ReadOnlyHint).IsTrue();
        await Assert.That(Tool("kcap-flows", "list_reviewer_vendors").Annotations.ReadOnlyHint).IsTrue();
        await Assert.That(Tool("kcap-flows", "list_flow_definitions").Annotations.ReadOnlyHint).IsTrue();

        var declare = Tool("kcap-plans", "declare_plan_document").Annotations;
        await Assert.That(declare.ReadOnlyHint).IsFalse();
        await Assert.That(declare.DestructiveHint).IsFalse();
        await Assert.That(declare.IdempotentHint).IsTrue();
        await Assert.That(declare.OpenWorldHint).IsFalse();

        // Replacing the task list drops entries not in it and mints ids for entries carrying none.
        var setTasks = Tool("kcap-plans", "set_plan_tasks").Annotations;
        await Assert.That(setTasks.DestructiveHint).IsTrue();
        await Assert.That(setTasks.IdempotentHint).IsFalse();
        await Assert.That(Tool("kcap-workitems", "detach_work_item").Annotations.DestructiveHint).IsTrue();
        await Assert.That(Tool("kcap-memory", "save_memory").Annotations.ReadOnlyHint).IsFalse();
        // Changing who may see a memory overwrites its access scope.
        await Assert.That(Tool("kcap-memory", "rescope_memory").Annotations.DestructiveHint).IsTrue();
        // The server keys a declaration on its normalized text, so re-declaring lands on the same end.
        await Assert.That(Tool("kcap-workitems", "declare_loose_end").Annotations.IdempotentHint).IsTrue();
        // A status read acknowledges the pending messages it rendered, so it is not a pure read.
        await Assert.That(Tool("kcap-flows", "get_flow_status").Annotations.ReadOnlyHint).IsFalse();
        await Assert.That(Tool("kcap-flows", "get_review_flow_status").Annotations.ReadOnlyHint).IsFalse();
        // A hosted agent acts on its own once launched.
        await Assert.That(Tool("kcap-flows", "start_review_flow").Annotations.OpenWorldHint).IsTrue();
    }

    [Test]
    public async Task Annotations_serialize_camel_cased_with_unset_hints_omitted() {
        var result = new McpToolsResult([new("get_plan", "d", new("object", new(), []), McpToolAnnotations.Read)]);
        var json   = JsonSerializer.Serialize(result, McpJsonContext.Default.McpToolsResult);

        await Assert.That(json).Contains("\"annotations\":{\"readOnlyHint\":true,\"openWorldHint\":false}");
    }
}

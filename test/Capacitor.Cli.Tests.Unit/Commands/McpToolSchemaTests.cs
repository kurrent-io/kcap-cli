using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Every tool any kcap MCP server publishes, found by reflection so a new server's list is covered without
/// being named here: each schema says it takes nothing undeclared, and the stdio loop's check refuses exactly that.</summary>
public class McpToolSchemaTests {
    /// Every parameterless static factory in the CLI assembly that returns a tool list.
    static IEnumerable<(string Source, McpTool Tool)> PublishedTools() =>
        typeof(McpStdioLoop).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(method => method.ReturnType == typeof(McpTool[]) && method.GetParameters().Length == 0)
            .SelectMany(method => ((McpTool[])method.Invoke(null, null)!).Select(tool => ($"{method.DeclaringType!.Name}.{method.Name}", tool)));

    static JsonObject CallTo(McpTool tool, JsonObject arguments) => new() {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = arguments },
    };

    [Test]
    public async Task Every_server_publishes_its_tools() =>
        await Assert.That(PublishedTools().Select(p => p.Source).Distinct().Count()).IsGreaterThanOrEqualTo(14);

    [Test]
    public async Task Every_published_schema_takes_no_undeclared_argument() {
        foreach (var (source, tool) in PublishedTools()) {
            var schema = JsonNode.Parse(JsonSerializer.Serialize(new McpToolsResult([tool]), McpJsonContext.Default.McpToolsResult))!
                ["tools"]![0]!["inputSchema"]!;

            await Assert.That(schema["additionalProperties"]?.ToJsonString()).IsEqualTo("false").Because($"{source}: {tool.Name}");
        }
    }

    [Test]
    public async Task Every_published_tool_refuses_an_undeclared_argument_and_takes_its_declared_ones() {
        foreach (var (source, tool) in PublishedTools()) {
            var declared = new JsonObject();
            foreach (var key in tool.InputSchema.Properties.Keys) declared[key] = "x";

            await Assert.That(McpToolArguments.UndeclaredArguments([tool], CallTo(tool, declared))).IsNull()
                .Because($"{source}: {tool.Name}");
            await Assert.That(McpToolArguments.UndeclaredArguments([tool], CallTo(tool, new JsonObject { ["not_declared"] = 1 })))
                .Contains($"{tool.Name} does not take not_declared").Because($"{source}: {tool.Name}");
        }
    }
}

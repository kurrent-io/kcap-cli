using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Every tool any kcap MCP server publishes, found by reflection so a new server's list is covered without
/// being named here: the stdio loop's check accepts exactly the arguments the published schema lists, so what a
/// client is told and what it is held to cannot drift apart.</summary>
public class McpToolSchemaTests {
    /// Every parameterless static factory in the CLI assembly that returns a tool list.
    static IEnumerable<(string Source, McpTool Tool)> PublishedTools() =>
        typeof(McpStdioLoop).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(method => method.ReturnType == typeof(McpTool[]) && method.GetParameters().Length == 0)
            .SelectMany(method => ((McpTool[])method.Invoke(null, null)!).Select(tool => ($"{method.DeclaringType!.Name}.{method.Name}", tool)));

    /// The schema as a client receives it.
    static JsonObject PublishedSchema(McpTool tool) =>
        JsonNode.Parse(JsonSerializer.Serialize(new McpToolsResult([tool]), McpJsonContext.Default.McpToolsResult))!
            ["tools"]![0]!["inputSchema"]!.AsObject();

    static JsonObject CallTo(McpTool tool, JsonObject arguments) => new() {
        ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
        ["params"] = new JsonObject { ["name"] = tool.Name, ["arguments"] = arguments },
    };

    [Test]
    public async Task Every_server_publishes_its_tools() =>
        await Assert.That(PublishedTools().Select(p => p.Source).Distinct().Count()).IsGreaterThanOrEqualTo(14);

    [Test]
    public async Task Every_published_tool_takes_exactly_the_arguments_its_schema_lists() {
        foreach (var (source, tool) in PublishedTools()) {
            var schema = PublishedSchema(tool);
            var listed = new JsonObject();
            foreach (var (key, _) in schema["properties"]!.AsObject()) listed[key] = "x";

            await Assert.That(McpToolArguments.UndeclaredArguments([tool], CallTo(tool, listed))).IsNull()
                .Because($"{source}: {tool.Name}");
            await Assert.That(McpToolArguments.UndeclaredArguments([tool], CallTo(tool, new JsonObject { ["not_declared"] = 1 })))
                .Contains($"{tool.Name} does not take not_declared").Because($"{source}: {tool.Name}");
            await Assert.That(schema.ContainsKey("additionalProperties")).IsFalse().Because($"{source}: {tool.Name}");
        }
    }
}

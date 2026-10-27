using System.Text.RegularExpressions;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

/// <summary>A user-run OpenCode tells kcap's MCP servers nothing about the calling session, so the
/// plugin stamps it on the tools whose session_id names the caller, when the call names none.</summary>
public partial class OpenCodeSessionStampTests {
    const string Content = OpenCodeExtensionInstaller.ExtensionContent;

    static readonly (string Server, McpTool[] Tools)[] Servers = [
        ("kcap-workitems", McpWorkItemsServer.BuildToolsList()),
        ("kcap-plans",     McpPlansServer.BuildToolsList()),
        ("kcap-flows",     McpFlowsServer.BuildToolsList()),
    ];

    [GeneratedRegex("""const CALLER_SESSION_TOOLS = new Set\(\[(?<list>[^\]]*)\]\)""")]
    private static partial Regex StampedToolsLiteral();

    static IReadOnlyList<string> StampedTools() =>
        [.. Regex.Matches(StampedToolsLiteral().Match(Content).Groups["list"].Value, "\"([^\"]+)\"").Select(m => m.Groups[1].Value)];

    static string Hook() => Content[Content.IndexOf("\"tool.execute.before\"", StringComparison.Ordinal)..];

    /// <summary>A name OpenCode would never present stamps nothing, silently; the server must declare the
    /// tool and the tool must take a session_id.</summary>
    [Test]
    public async Task Every_stamped_tool_is_a_kcap_tool_that_takes_a_session_id() {
        var stamped = StampedTools();
        await Assert.That(stamped).IsNotEmpty();

        foreach (var name in stamped) {
            var tool = Servers.SelectMany(s => s.Tools.Select(t => (Name: $"{s.Server}_{t.Name}", t.InputSchema)))
                .SingleOrDefault(t => t.Name == name);

            await Assert.That(tool.InputSchema?.Properties.ContainsKey("session_id")).IsTrue().Because(name);
        }
    }

    /// <summary>On list_loose_ends session_id is a filter, so a default narrows "my loose ends" to this
    /// session; close_loose_end retries without a session only one it defaulted itself.</summary>
    [Test]
    [Arguments("kcap-workitems_list_loose_ends")]
    [Arguments("kcap-workitems_close_loose_end")]
    public async Task A_tool_whose_session_id_is_not_the_caller_is_never_stamped(string tool) =>
        await Assert.That(StampedTools()).DoesNotContain(tool);

    /// <summary>The stamp lands on the object OpenCode sends on, never a replacement it would not see; a
    /// session_id the call names wins; and only a session proven top-level is stamped, waiting for one
    /// still being classified rather than stamping what may be a subagent.</summary>
    [Test]
    public async Task The_stamp_mutates_the_args_only_for_a_missing_session_on_a_proven_top_level_session() {
        var hook = Hook();

        await Assert.That(hook).Contains("""if (args.session_id !== undefined && args.session_id !== null && args.session_id !== "") return""");
        await Assert.That(hook).Contains("const pending = pendingStart.get(sid)");
        await Assert.That(hook).Contains("if (!started.has(sid) || children.has(sid)) return");
        await Assert.That(hook).Contains("args.session_id = sid");
        await Assert.That(hook).DoesNotContain("output.args =");
        await Assert.That(hook).Contains("} catch {");
    }
}

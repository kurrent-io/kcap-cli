using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiReviewerToolSurfaceTests {
    static AcpMcpServerSpec Server(string name) => new(name, "/usr/local/bin/kcap", ["mcp", "x"], []);

    static readonly AcpMcpServerSpec ResultChannel = Server(KcapMcpRegistry.ReservedResultChannelId);

    [Test]
    public async Task File_tools_come_first_then_the_result_channel_in_registry_order() {
        var tools = PiReviewerToolSurface.For([ResultChannel]);

        await Assert.That(tools.Select(t => t.PiName)).IsEquivalentTo(
            new[] { "read_file", "list_directory", "search_files", "submit_review_result", "send_flow_message" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Result_channel_tools_keep_their_bare_names() {
        var tool = PiReviewerToolSurface.For([ResultChannel]).Single(t => t.McpName == "submit_review_result");

        await Assert.That(tool.PiName).IsEqualTo("submit_review_result");
        await Assert.That(tool.ServerName).IsEqualTo(KcapMcpRegistry.ReservedResultChannelId);
    }

    [Test]
    public async Task Allowlisted_server_tools_are_prefixed_and_sorted() {
        var names = PiReviewerToolSurface.For([ResultChannel, Server("kcap-review")])
            .Where(t => t.ServerName == "kcap-review").Select(t => t.PiName).ToArray();

        await Assert.That(names).Contains("kcap_review_get_pr_summary");
        await Assert.That(names).IsEquivalentTo(names.OrderBy(n => n, StringComparer.Ordinal),
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_unclassified_server_throws() {
        await Assert.That(() => PiReviewerToolSurface.For([ResultChannel, Server("kcap-mystery")]))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("pi_reviewer_unclassified_server");
    }

    [Test]
    public async Task A_launch_without_the_result_channel_throws() {
        await Assert.That(() => PiReviewerToolSurface.For([Server("kcap-review")]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task No_tool_is_or_shadows_a_Pi_built_in() {
        var names = PiReviewerToolSurface.For([ResultChannel, Server("kcap-review"), Server("kcap-sessions")])
            .Select(t => t.PiName).ToArray();

        await Assert.That(names.Intersect(PiReviewerToolSurface.PiBuiltInNames)).IsEmpty();
        await Assert.That(names).HasDistinctItems();
    }

    [Test]
    public async Task The_allowlist_argument_is_the_same_list_comma_joined() {
        var tools = PiReviewerToolSurface.For([ResultChannel]);

        await Assert.That(PiReviewerToolSurface.AllowlistArg(tools))
            .IsEqualTo("read_file,list_directory,search_files,submit_review_result,send_flow_message");
    }
}

using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class LaunchStagesTests {
    [Test]
    [Arguments(null, "Launch accepted")]
    [Arguments("spawned", "Process started")]
    [Arguments("initialized", "Initialized")]
    [Arguments("session_created", "Session created")]
    [Arguments("model_set", "Model set")]
    [Arguments("thread_started", "Thread started")]
    [Arguments("thread_resumed", "Thread resumed")]
    public async Task A_known_stage_reads_as_its_label(string? stage, string label) {
        await Assert.That(LaunchStages.Label(stage)).IsEqualTo(label);
    }

    /// The stage vocabulary is the runtimes' own and open, so an unknown token still reads.
    [Test]
    public async Task An_unknown_stage_is_humanized() {
        await Assert.That(LaunchStages.Label("mcp_servers_ready")).IsEqualTo("Mcp servers ready");
    }

    [Test]
    public async Task The_starting_line_names_the_vendor_and_the_stage() {
        await Assert.That(LaunchStages.StartingText("claude", "spawned")).IsEqualTo("Starting Claude · Process started");
    }
}

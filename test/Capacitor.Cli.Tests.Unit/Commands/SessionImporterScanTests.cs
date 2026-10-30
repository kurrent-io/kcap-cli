using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionImporterScanTests {
    [Test]
    public async Task ScanAgentLifecycle_ExtractsSubagentTypeFromAsyncLaunchedInvocation() {
        var transcript = """
                         {"type":"user","message":{"content":"go"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"code-reviewer","prompt":"review this"}}]}}
                         {"type":"result","tool_use_id":"toolu_1","tool_result":{"status":"async_launched","agentId":"agent-abc"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent.ContainsKey("agent-abc")).IsTrue();
        await Assert.That(scan.AgentTypeByAgent["agent-abc"]).IsEqualTo("code-reviewer");
    }

    [Test]
    public async Task ScanAgentLifecycle_ExtractsSubagentTypeFromForegroundToolUseResult() {
        var transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"agentId":"agent-xyz"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.AgentTypeByAgent["agent-xyz"]).IsEqualTo("general-purpose");
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentSeenFirstInAgentProgress_StillResolvesTypeFromLaterInvocation() {
        // Regression: when agent_progress for an agentId arrives before the assistant
        // tool_use + async_launched pair, the early-return on FirstLineByAgent.ContainsKey
        // used to prevent subagent_type from being copied into AgentTypeByAgent. (Qodo#29)
        var transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-abc"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"code-reviewer"}}]}}
                         {"type":"result","tool_use_id":"toolu_1","tool_result":{"status":"async_launched","agentId":"agent-abc"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent["agent-abc"]).IsEqualTo(0); // progress line wins the position
        await Assert.That(scan.AgentTypeByAgent["agent-abc"]).IsEqualTo("code-reviewer");
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentSeenFirstInAgentProgress_StillResolvesTypeFromForegroundToolUseResult() {
        // Same regression as above, but for the foreground toolUseResult path.
        var transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-xyz"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"agentId":"agent-xyz"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent["agent-xyz"]).IsEqualTo(0);
        await Assert.That(scan.AgentTypeByAgent["agent-xyz"]).IsEqualTo("general-purpose");
    }

    [Test]
    public async Task ScanAgentLifecycle_AgentWithoutInvocation_HasNoType() {
        // progress-only reference — no parent tool_use, so the subagent_type is unknown.
        var transcript = """
                         {"type":"progress","data":{"type":"agent_progress","agentId":"agent-orphan"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.tmp", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.FirstLineByAgent.ContainsKey("agent-orphan")).IsTrue();
        await Assert.That(scan.AgentTypeByAgent.ContainsKey("agent-orphan")).IsFalse();
    }

    /// <summary>A parent whose result never names its agent: the meta file beside the subagent's transcript
    /// is what ties the two, which seats the agent at its call and names the call on its start.</summary>
    [Test]
    public async Task ScanAgentLifecycle_LinksAnAgentToItsCallThroughItsMetaFile() {
        var transcript = """
                         {"type":"user","message":{"content":"go"}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{"subagent_type":"Explore","prompt":"map it"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1"}]},"toolUseResult":{"totalDurationMs":1000}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript);
        tmp.CreateFile(["transcript", "subagents", "agent-a1.jsonl"], "{}");
        tmp.CreateFile(["transcript", "subagents", "agent-a1.meta.json"], """{"toolUseId":"toolu_1"}""");

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.SpawnCallByAgent["a1"]).IsEqualTo("toolu_1");
        await Assert.That(scan.FirstLineByAgent["a1"]).IsEqualTo(1);
        await Assert.That(scan.AgentTypeByAgent["a1"]).IsEqualTo("Explore");
    }

    [Test]
    public async Task ScanAgentLifecycle_LinksAForegroundAgentToTheCallItsResultAnswers() {
        var transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Agent","input":{"subagent_type":"general-purpose"}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_2"}]},"toolUseResult":{"status":"completed","agentId":"agent-xyz"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript);

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.SpawnCallByAgent["agent-xyz"]).IsEqualTo("toolu_2");
    }

    /// <summary>Evidence that disagrees, or one call claimed by two agents, names no call at all: a wrong
    /// link would open another subagent's transcript from the row.</summary>
    [Test]
    public async Task ScanAgentLifecycle_LeavesConflictingOrSharedCallsUnlinked() {
        var transcript = """
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Task","input":{}}]}}
                         {"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_2","name":"Task","input":{}}]}}
                         {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1"}]},"toolUseResult":{"status":"completed","agentId":"a1"}}
                         """;

        using var tmp = new TempDir();
        var path = tmp.CreateFile("transcript.jsonl", transcript);
        tmp.CreateFile(["transcript", "subagents", "agent-a1.meta.json"], """{"toolUseId":"toolu_2"}""");
        tmp.CreateFile(["transcript", "subagents", "agent-b1.meta.json"], """{"toolUseId":"toolu_9"}""");
        tmp.CreateFile(["transcript", "subagents", "agent-c1.meta.json"], """{"toolUseId":"toolu_2"}""");
        tmp.CreateFile(["transcript", "subagents", "agent-d1.meta.json"], """{"toolUseId":"toolu_2"}""");

        var scan = SessionImporter.ScanAgentLifecycle(path);

        await Assert.That(scan.SpawnCallByAgent).IsEmpty();
        await Assert.That(scan.FirstLineByAgent.ContainsKey("c1")).IsFalse();
    }
}

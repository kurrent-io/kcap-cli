namespace Kapacitor.Cli.Tests.Unit;

public class WatcherManagerSpawnArgsTests {
    [Test]
    public async Task BuildSpawnArgs_default_vendor_omits_flag() {
        var args = WatcherManager.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "claude"
        );

        await Assert.That(args).Contains("watch abc \"/tmp/t.jsonl\"");
        await Assert.That(args).DoesNotContain("--vendor");
    }

    [Test]
    public async Task BuildSpawnArgs_codex_vendor_appends_flag() {
        var args = WatcherManager.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "codex"
        );

        // Fix #4: vendor must be quoted the same way transcriptPath/cwd are.
        await Assert.That(args).Contains("--vendor \"codex\"");
    }

    [Test]
    public async Task BuildSpawnArgs_vendor_with_spaces_is_quoted() {
        var args = WatcherManager.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "my vendor"
        );

        await Assert.That(args).Contains("--vendor \"my vendor\"");
    }

    [Test]
    public async Task BuildSpawnArgs_with_agent_uses_session_override() {
        var args = WatcherManager.BuildSpawnArgs(
            key: "sess-agent", transcriptPath: "/tmp/t.jsonl",
            agentId: "agent1", sessionIdOverride: "sess",
            cwd: "/repo", skipTitle: true, parentPid: 4242, vendor: "claude"
        );

        await Assert.That(args).Contains("watch sess \"/tmp/t.jsonl\" --agent-id agent1");
        await Assert.That(args).Contains("--cwd \"/repo\"");
        await Assert.That(args).Contains("--skip-title");
        await Assert.That(args).Contains("--parent-pid 4242");
    }
}

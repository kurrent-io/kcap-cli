namespace Capacitor.Cli.Tests.Unit;

public class WatcherSpawnArgsTests {
    /// <summary>Every spawn names its vendor, including the one `kcap watch` would have defaulted
    /// to: a reader of the command line never has to know what that default is.</summary>
    [Test]
    public async Task BuildSpawnArgs_names_the_default_vendor() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "claude"
        );

        await Assert.That(args).Contains("watch abc \"/tmp/t.jsonl\"");
        await Assert.That(args).Contains("--vendor \"claude\"");
    }

    [Test]
    public async Task BuildSpawnArgs_codex_vendor_appends_flag() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "codex"
        );

        await Assert.That(args).Contains("--vendor \"codex\"");
    }

    [Test]
    public async Task BuildSpawnArgs_vendor_with_spaces_is_quoted() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "my vendor"
        );

        await Assert.That(args).Contains("--vendor \"my vendor\"");
    }

    [Test]
    public async Task BuildSpawnArgs_with_agent_uses_session_override() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
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

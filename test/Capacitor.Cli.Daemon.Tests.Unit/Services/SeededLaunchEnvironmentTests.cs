using System.Diagnostics;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Harness.Muse;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Every launch whose child environment is seeded from the daemon's own: the session ids of the
/// shell the daemon started in are not passed on, and the agent is named for the session lookup. The
/// inherited ids are seeded here, so a child without them is the launch's own doing.</summary>
[NotInParallel]
public class SeededLaunchEnvironmentTests {
    const string Inherited = "inherited-from-the-daemons-shell";

    static RuntimeStartContext Ctx(string vendor) => new(
        AgentId: "agent-1", Vendor: vendor, SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
        Model: null, Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        DaemonName: "daemon-a");

    static IDictionary<string, string?> Launch(string runtime) => runtime switch {
        "acp"         => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(AcpVendorDescriptors.Cursor, new DaemonConfig(), Ctx("cursor")).Environment,
        "pi"          => PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx("pi")).Environment,
        "muse"        => MuseHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx("muse"), reviewer: false).Environment,
        "antigravity" => AntigravityHostedAgentRuntimeFactory.BuildTurnPsi(
                             new DaemonConfig(), Ctx("antigravity"), "prompt", conversationId: null, home: "/tmp/kcap-antigravity-home").Environment,
        "codex"       => CodexChildEnvironment(),
        _             => throw new ArgumentOutOfRangeException(nameof(runtime), runtime, null),
    };

    static IDictionary<string, string?> CodexChildEnvironment() {
        var env = new ProcessStartInfo().Environment;
        CodexHostedAgentRuntimeFactory.ApplyChildEnv(env, CodexHostedAgentRuntimeFactory.BuildEnv(Ctx("codex"), emitEnvelopeTranscript: false), remove: []);

        return env;
    }

    [Test]
    [Arguments("acp")]
    [Arguments("pi")]
    [Arguments("muse")]
    [Arguments("antigravity")]
    [Arguments("codex")]
    public async Task A_seeded_launch_drops_inherited_sessions_and_names_its_agent(string runtime) {
        using var claude = EnvScope.Exclusive("CLAUDE_CODE_SESSION_ID", Inherited);
        using var kcap   = EnvScope.Exclusive("KCAP_SESSION_ID", Inherited);
        using var codex  = EnvScope.Exclusive("CODEX_THREAD_ID", Inherited);

        var env = Launch(runtime);
        string? Carried(string name) => env.TryGetValue(name, out var value) ? value : null;

        await Assert.That(Carried("CLAUDE_CODE_SESSION_ID")).IsNotEqualTo(Inherited);
        await Assert.That(Carried("KCAP_SESSION_ID")).IsNotEqualTo(Inherited);
        await Assert.That(Carried("CODEX_THREAD_ID")).IsNotEqualTo(Inherited);
        await Assert.That(Carried("KCAP_DAEMON_AGENT_ID")).IsEqualTo("agent-1");
        await Assert.That(Carried("KCAP_DAEMON_NAME")).IsEqualTo("daemon-a");
    }
}

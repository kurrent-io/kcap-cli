namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins the requester-context resolution an MCP stdio server uses to answer "which session am I
/// serving, and where is it working?".
///
/// <para>The live failure these cover: a driver session launched from another session's shell
/// inherits the launcher's <c>KCAP_SESSION_ID</c> and (when the MCP registration pins no cwd) the
/// launcher's working directory. Nothing rewrites either for the child's MCP servers, so every flow
/// the driver started was attributed to the PARENT session, and the reviewer was handed the parent's
/// checkout — silently producing the wrong diff.</para>
///
/// <para>All of these inject the environment lookup rather than mutating the process environment, so
/// they stay parallel-safe (and can't be perturbed by whatever harness happens to be running the
/// suite — which is itself a session with these variables set).</para>
/// </summary>
public class HarnessRequesterContextTests {
    const string RunningDriverSession = "11111111-1111-1111-1111-111111111111";
    const string InheritedParentSession = "22222222222222222222222222222222";

    static Func<string, string?> Env(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var value) ? value : null;

    [Test]
    public async Task Resolves_the_running_harness_session_not_the_inherited_env_var() {
        // The shape of the live defect: an ambient KCAP_SESSION_ID naming the LAUNCHING session,
        // alongside the running harness's own per-process session id. The running one must win.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["KCAP_SESSION_ID"]        = InheritedParentSession,
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(resolved.SessionId).IsNotEqualTo(InheritedParentSession);
    }

    [Test]
    public async Task Resolves_the_running_harness_project_dir_so_the_caller_ignores_an_inherited_cwd() {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = "/repos/driver-worktree"
            }),
            directoryExists: path => path == "/repos/driver-worktree");

        await Assert.That(resolved.ProjectDir).IsEqualTo("/repos/driver-worktree");
    }

    [Test]
    public async Task Project_dir_that_no_longer_exists_degrades_to_the_process_cwd() {
        // A null ProjectDir is the caller's signal to keep using Directory.GetCurrentDirectory() —
        // sending the server a path nothing can be checked out at would be worse than the cwd.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = "/repos/deleted-worktree"
            }),
            directoryExists: _ => false);

        await Assert.That(resolved.ProjectDir).IsNull();
        // The session id is still resolved — an unusable directory says nothing about identity.
        await Assert.That(resolved.SessionId).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Blank_project_dir_is_treated_as_unreported(string projectDir) {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = projectDir
            }),
            // Deliberately permissive: a blank path must be rejected before it is probed, not
            // accepted because some filesystem stub said yes.
            directoryExists: _ => true);

        await Assert.That(resolved.ProjectDir).IsNull();
    }

    [Test]
    public async Task Without_a_harness_signal_the_ambient_kcap_session_is_still_used() {
        // Every non-Claude-Code harness keeps the pre-existing behaviour: KCAP_SESSION_ID is the
        // best (and only) evidence available, and no project dir is claimed.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() { ["KCAP_SESSION_ID"] = InheritedParentSession }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo(InheritedParentSession);
        await Assert.That(resolved.ProjectDir).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("  ")]
    public async Task Blank_harness_session_falls_through_to_the_ambient_signals(string harnessSessionId) {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = harnessSessionId,
                ["CODEX_THREAD_ID"]        = "abc-def"
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo("abc-def"); // opaque: canonicalization keeps it
        await Assert.That(resolved.ProjectDir).IsNull();
    }

    [Test]
    public async Task Ambient_precedence_between_the_non_harness_signals_is_unchanged() {
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["KCAP_SESSION_ID"] = InheritedParentSession,
                ["CODEX_THREAD_ID"] = "abc-def"
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsEqualTo(InheritedParentSession);
    }

    [Test]
    public async Task A_nested_harness_signal_makes_the_claude_evidence_unprovable() {
        // codex-inside-claude (or the reverse): the inner harness inherits the outer's variables and
        // leaves them in place, so relocating the requester to CLAUDE_PROJECT_DIR could point at the
        // OUTER session's checkout. Resolution must fall back to the ambient behaviour instead —
        // never worse than what shipped before, which is the whole point of the guard.
        var resolved = HarnessRequesterContext.Resolve(
            Env(new() {
                ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
                ["CLAUDE_PROJECT_DIR"]     = "/repos/outer-harness-checkout",
                ["CODEX_THREAD_ID"]        = "77777777-7777-7777-7777-777777777777",
                ["KCAP_SESSION_ID"]        = InheritedParentSession
            }),
            directoryExists: _ => true);

        await Assert.That(resolved.ProjectDir).IsNull();
        await Assert.That(resolved.SessionId).IsEqualTo(InheritedParentSession);
    }

    [Test]
    public async Task No_signal_at_all_resolves_nothing() {
        var resolved = HarnessRequesterContext.Resolve(Env(new()), directoryExists: _ => true);

        await Assert.That(resolved.SessionId).IsNull();
        await Assert.That(resolved.ProjectDir).IsNull();
    }

    const string GrokSession = "01a1378d-a97a-7420-88e0-65e6248fe6d2";

    static Func<CodingAgentAncestor?> Nearest(string? vendor) => () => vendor is null ? null : new CodingAgentAncestor(vendor, 4242);

    static HarnessRequesterContext.Resolved ResolveUnder(
            string? nearest, Dictionary<string, string?> env, string? grokCwd = null, Func<string, string?>? daemon = null) =>
        HarnessRequesterContext.Resolve(
            Env(env), directoryExists: _ => true, Nearest(nearest), processCwd: pid => pid == 4242 ? grokCwd : null,
            hostedAgentSession: daemon ?? (_ => null));

    /// <summary>Grok exports its own session to every MCP server it starts, but also passes through the
    /// Claude and kcap ids of the shell that launched it; under Grok, Grok's id is the requester, working
    /// in Grok's own directory.</summary>
    [Test]
    public async Task Under_grok_the_grok_session_and_directory_win_over_inherited_claude_and_kcap_ones() {
        var resolved = ResolveUnder("grok", new() {
            ["GROK_SESSION_ID"]        = GrokSession,
            ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
            ["CLAUDE_PROJECT_DIR"]     = "/repos/claude-checkout",
            ["KCAP_SESSION_ID"]        = InheritedParentSession,
        }, grokCwd: "/repos/grok-checkout");

        await Assert.That(resolved.SessionId).IsEqualTo("01a1378da97a742088e065e6248fe6d2");
        await Assert.That(resolved.ProjectDir).IsEqualTo("/repos/grok-checkout");
    }

    /// <summary>A Claude Code session started from Grok's shell inherits Grok's id; its own wins.</summary>
    [Test]
    public async Task Under_claude_an_inherited_grok_session_is_ignored() {
        var resolved = ResolveUnder("claude", new() {
            ["GROK_SESSION_ID"]        = GrokSession,
            ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
        });

        await Assert.That(resolved.SessionId).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    public async Task Under_another_harness_an_inherited_grok_session_is_ignored() {
        var resolved = ResolveUnder("codex", new() {
            ["GROK_SESSION_ID"] = GrokSession,
            ["KCAP_SESSION_ID"] = InheritedParentSession,
        });

        await Assert.That(resolved.SessionId).IsEqualTo(InheritedParentSession);
    }

    /// <summary>With a Grok id present and no readable agent above, whose the inherited ids are cannot be
    /// told, so none is claimed rather than one being attributed to the wrong session.</summary>
    [Test]
    public async Task An_unreadable_ancestry_claims_no_session() {
        var resolved = ResolveUnder(null, new() {
            ["GROK_SESSION_ID"]        = GrokSession,
            ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession,
            ["KCAP_SESSION_ID"]        = InheritedParentSession,
        });

        await Assert.That(resolved.SessionId).IsNull();
        await Assert.That(resolved.ProjectDir).IsNull();
    }

    [Test]
    public async Task The_process_tree_is_not_walked_without_a_grok_session() {
        var walks = 0;
        HarnessRequesterContext.Resolve(
            Env(new() { ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession }),
            directoryExists: _ => true,
            nearestAgent: () => { walks++; return new CodingAgentAncestor("grok", 1); },
            processCwd: _ => null,
            hostedAgentSession: _ => null);

        await Assert.That(walks).IsEqualTo(0);
    }

    const string HostedAgentId = "agent-7";
    const string BoundSession  = "ses_01JC7QY3VZ8K";

    /// <summary>What the hosting daemon answers for <see cref="HostedAgentId"/>: null is an agent it has
    /// not bound yet, or a daemon that cannot be asked.</summary>
    static Func<string, string?> Daemon(string? bound) => agentId => agentId == HostedAgentId ? bound : null;

    public static IEnumerable<Func<(string Row, string? Nearest, Dictionary<string, string?> Env, string? Bound, string? Expected)>> HostedAgentRows() {
        yield return () => ("a bound agent resolves to its session", "opencode",
            new() { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId }, BoundSession, BoundSession);
        yield return () => ("the agent id hooks read is not the lookup's", "opencode",
            new() { ["KCAP_AGENT_ID"] = HostedAgentId }, BoundSession, null);
        yield return () => ("an unbound agent or an unreachable daemon resolves to nothing", "opencode",
            new() { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId }, null, null);
        yield return () => ("the agent's session wins over an inherited KCAP_SESSION_ID", "opencode",
            new() { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId, ["KCAP_SESSION_ID"] = InheritedParentSession }, BoundSession, BoundSession);
        yield return () => ("an unbound agent keeps the inherited KCAP_SESSION_ID", "opencode",
            new() { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId, ["KCAP_SESSION_ID"] = InheritedParentSession }, null, InheritedParentSession);
        yield return () => ("Claude's own session wins over the agent's", "claude",
            new() { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId, ["CLAUDE_CODE_SESSION_ID"] = RunningDriverSession }, BoundSession,
            "11111111111111111111111111111111");
        yield return () => ("Codex's own session wins over the agent's", "codex",
            new() { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId, ["CODEX_THREAD_ID"] = "thread-1" }, BoundSession, "thread-1");
        yield return () => ("Grok's own session wins over the agent's", "grok",
            new() { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId, ["GROK_SESSION_ID"] = GrokSession }, BoundSession,
            "01a1378da97a742088e065e6248fe6d2");
    }

    /// <summary>A daemon-hosted agent whose harness exports no session of its own is asked about by its
    /// agent id, below every harness's own evidence and above the ambient <c>KCAP_SESSION_ID</c>.</summary>
    [Test]
    [MethodDataSource(nameof(HostedAgentRows))]
    public async Task A_hosted_agent_resolves_through_its_daemon(
            string row, string? nearest, Dictionary<string, string?> env, string? bound, string? expected) {
        var resolved = ResolveUnder(nearest, env, grokCwd: "/repos/grok-checkout", daemon: Daemon(bound));

        await Assert.That(resolved.SessionId).IsEqualTo(expected).Because(row);
    }

    /// <summary>An agent's MCP server starts before the agent is bound, so an empty answer must not stick;
    /// once bound, the session is kept and the daemon is not asked again.</summary>
    [Test]
    public async Task An_unbound_answer_is_asked_again_and_a_bound_one_is_kept() {
        var answers  = new Queue<string?>([null, BoundSession, "ses_never_asked"]);
        var asks     = 0;
        var sessions = new HostedAgentSessions(_ => { asks++; return answers.Dequeue(); });
        var env      = new Dictionary<string, string?> { ["KCAP_DAEMON_AGENT_ID"] = HostedAgentId };

        string? Call() => ResolveUnder("opencode", env, daemon: sessions.For).SessionId;

        await Assert.That(Call()).IsNull();
        await Assert.That(Call()).IsEqualTo(BoundSession);
        await Assert.That(Call()).IsEqualTo(BoundSession);
        await Assert.That(asks).IsEqualTo(2);
    }
}

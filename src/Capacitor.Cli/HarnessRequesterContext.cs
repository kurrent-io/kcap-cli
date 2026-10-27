using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Answers "which session am I serving, and where is it working?" for a long-lived MCP server
/// process, from what the RUNNING harness told this process — not from whatever environment the
/// process happened to inherit.
///
/// <para>The distinction matters because an MCP stdio server is spawned once, at harness startup,
/// from the launching process's environment, and then serves every tool call for the rest of that
/// session:</para>
/// <list type="bullet">
///   <item><description><c>KCAP_SESSION_ID</c> is written by the session-start hook into the
///   harness's shell-env file, which the harness applies to its <b>shell</b> invocations. It is
///   therefore correct inside a session's own shell, but an MCP server never sees that file — it
///   only sees the value that was exported in whichever process launched the harness. When one
///   session launches another (a driver session started from a parent session's shell), the child
///   harness — and so every MCP server it spawns — inherits the <b>parent's</b>
///   <c>KCAP_SESSION_ID</c>, and nothing ever rewrites it. Requester context resolved from it then
///   names the wrong session for the whole life of the driver.</description></item>
///   <item><description>The process's current directory has the same failure mode whenever the MCP
///   registration does not pin a <c>cwd</c>: the server inherits the launching process's directory,
///   which for a session launched from another session's checkout is the <b>parent's</b> checkout,
///   not the driver's.</description></item>
/// </list>
///
/// <para>Claude Code exports <c>CLAUDE_CODE_SESSION_ID</c> and <c>CLAUDE_PROJECT_DIR</c> into every
/// process it spawns, setting both to <b>its own</b> session and project directory — so a child
/// harness overwrites any inherited values for its own children. That makes them per-process
/// evidence of the harness we are actually running under, which is exactly what an inherited
/// variable cannot be, and when present it wins.</para>
///
/// <para>Nesting keeps that evidence honest in one direction only: a Claude Code session rewrites
/// these variables for its own children, but a DIFFERENT harness launched from inside a Claude Code
/// session (<c>codex exec</c> from a Bash tool call, say) inherits them and leaves them in place. So
/// a co-present signal from another harness makes ours unprovable, and resolution falls back to the
/// ambient signals rather than relocating the requester into the OUTER harness's checkout.</para>
///
/// <para>A daemon-hosted agent whose harness names no session of its own carries
/// <c>KCAP_DAEMON_AGENT_ID</c> instead, and its daemon is asked which session that agent is bound to.</para>
/// </summary>
static class HarnessRequesterContext {
    /// <summary>Set by the running Claude Code process in every child process's environment.</summary>
    internal const string ClaudeSessionIdVar = "CLAUDE_CODE_SESSION_ID";

    /// <summary>The running Claude Code session's project directory, set alongside the id above.</summary>
    internal const string ClaudeProjectDirVar = "CLAUDE_PROJECT_DIR";

    /// <summary>Another harness's own-session signal. Its presence alongside the Claude variables
    /// means one harness is nested inside the other and neither is provably ours.</summary>
    internal const string CodexThreadIdVar = "CODEX_THREAD_ID";

    /// <summary>Set by Grok on every process it starts, its MCP servers included, to its own session.
    /// It is passed through to anything those processes start too, so it is trusted only when Grok is
    /// the nearest harness above this process.</summary>
    internal const string GrokSessionIdVar = "GROK_SESSION_ID";

    internal static string? GrokSessionIn(Func<string, string?> getEnv) =>
        getEnv(GrokSessionIdVar) is { } id && !string.IsNullOrWhiteSpace(id) ? id : null;

    /// <param name="SessionId">
    /// The requesting session in the canonical form the server files it under (a GUID as its
    /// 32-hex form, an opaque vendor id unchanged). Null when no harness signal is available.
    /// </param>
    /// <param name="ProjectDir">
    /// The running harness's project directory, or null when the harness did not report one (or
    /// reported one that no longer exists). Null means "no better answer than the process cwd".
    /// </param>
    internal readonly record struct Resolved(string? SessionId, string? ProjectDir);

    internal static Resolved Resolve() => Resolve(Environment.GetEnvironmentVariable, Directory.Exists);

    internal static Resolved Resolve(Func<string, string?> getEnv, Func<string, bool> directoryExists) =>
        Resolve(getEnv, directoryExists, ProcessHelpers.NearestCodingAgent, ProcessHelpers.GetProcessCwd, HostedAgentSessions.Daemon.For);

    /// <param name="nearestAgent">The nearest coding agent above this process, asked only when a Grok
    /// session is in the environment.</param>
    /// <param name="processCwd">A process's working directory, which for Grok is its project.</param>
    /// <param name="hostedAgentSession">The session the hosting daemon has bound an agent id to, null
    /// when it has none or cannot be asked. Asked only when no harness names its own session.</param>
    internal static Resolved Resolve(
            Func<string, string?> getEnv, Func<string, bool> directoryExists,
            Func<CodingAgentAncestor?> nearestAgent, Func<int, string?> processCwd,
            Func<string, string?> hostedAgentSession) {
        if (GrokSessionIn(getEnv) is { } grokSessionId) {
            switch (nearestAgent()) {
                // Whose the inherited ids are cannot be told, so none of them is claimed.
                case null:
                    return new(SessionId: null, ProjectDir: null);
                case { Vendor: "grok" } grok:
                    var cwd = processCwd(grok.Pid);
                    return new(SessionIds.Canonical(grokSessionId), cwd is { Length: > 0 } && directoryExists(cwd) ? cwd : null);
            }
        }

        var harnessSessionId = getEnv(ClaudeSessionIdVar);
        var nestedHarness    = getEnv(CodexThreadIdVar);

        // No usable per-process harness evidence — either none was reported, or another harness's
        // own-session signal is co-present so ours is unprovable (see the nesting note above). No
        // project directory is claimed, so the caller keeps using the process cwd.
        if (string.IsNullOrWhiteSpace(harnessSessionId) || !string.IsNullOrWhiteSpace(nestedHarness)) {
            // A hosted agent's session outranks a KCAP_SESSION_ID inherited from whatever shell started
            // its daemon. A Codex thread id is that harness's own session, so it outranks the daemon.
            var hosted = string.IsNullOrWhiteSpace(nestedHarness) && getEnv(HostedAgent.DaemonAgentIdVar) is { Length: > 0 } agentId
                ? SessionIds.Canonical(hostedAgentSession(agentId))
                : null;

            return new(hosted ?? ArgParsing.ResolveSessionIdFromEnv(getEnv), ProjectDir: null);
        }

        // A project dir is only usable if it still resolves to a directory — a value pointing at a
        // removed worktree must degrade to the process cwd rather than send the server a path
        // nothing can be checked out at.
        var projectDir = getEnv(ClaudeProjectDirVar)?.Trim();
        var usableProjectDir = !string.IsNullOrEmpty(projectDir) && directoryExists(projectDir)
            ? projectDir
            : null;

        return new(SessionIds.Canonical(harnessSessionId), usableProjectDir);
    }
}

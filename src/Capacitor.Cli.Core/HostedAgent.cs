namespace Capacitor.Cli.Core;

/// <summary>
/// What the daemon exports into an agent it hosts, resolved once at the composition root: the
/// agent's id, whether the web app renders its session, and the bridge it answers on. The three are
/// independently absent — an ACP runtime stamps the id alone, and a daemon with no bridge names
/// none — so each is read on its own.
/// </summary>
/// <remarks>
/// The variable names are the contract between the two executables: the daemon writes them onto
/// every agent it spawns, this reads them back, and a name that drifts on either side fails
/// silently. Both sides take them from here.
/// </remarks>
public sealed record HostedAgent(string? AgentId, bool IsRendered, DaemonBridge Bridge) {
    public const string AgentIdVar   = "KCAP_AGENT_ID";
    public const string RenderedVar  = "KCAP_RENDERED_AGENT";
    public const string BridgeUrlVar = "KCAP_DAEMON_URL";

    /// <summary>The value <see cref="RenderedVar"/> carries when the web app renders the session.</summary>
    public const string Rendered = "1";

    /// <summary>Not hosted — what a terminal session gets.</summary>
    public static readonly HostedAgent Terminal = new(null, false, DaemonBridge.None);

    /// <summary>
    /// An exported variable can be empty, and a hook inherits its host's environment wholesale. An
    /// empty id is not an id: posted as <c>agent_host_id</c> it attributes the session to an agent
    /// nothing hosts. An id of blanks is still an id — nothing downstream reads it, so there is no
    /// call to guess at what was meant.
    /// </summary>
    public static HostedAgent FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(AgentIdVar) is { Length: > 0 } id ? id : null,
            Environment.GetEnvironmentVariable(RenderedVar) is Rendered,
            DaemonBridge.Parse(Environment.GetEnvironmentVariable(BridgeUrlVar)));
}

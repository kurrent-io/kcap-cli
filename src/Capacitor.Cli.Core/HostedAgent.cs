namespace Capacitor.Cli.Core;

/// <summary>
/// What the daemon exports into an agent it hosts, resolved once at the composition root: the
/// agent's id, whether the web app renders its session, the bridge it answers on, and the session
/// kind. Each is independently absent — an ACP runtime stamps the kind alone, and a daemon with no
/// bridge names none — so each is read on its own.
/// </summary>
/// <remarks>
/// The variable names are the contract between the two executables: the daemon writes them onto
/// every agent it spawns, this reads them back, and a name that drifts on either side fails
/// silently. Both sides take them from here.
/// </remarks>
public sealed record HostedAgent(string? AgentId, bool IsRendered, DaemonBridge Bridge) {
    public const string AgentIdVar     = "KCAP_AGENT_ID";
    public const string RenderedVar    = "KCAP_RENDERED_AGENT";
    public const string BridgeUrlVar   = "KCAP_DAEMON_URL";
    public const string SessionKindVar = "KCAP_SESSION_KIND";
    public const string FlowRoleVar    = "KCAP_FLOW_ROLE";
    public const string DaemonEpochVar = "KCAP_DAEMON_EPOCH";

    /// <summary>Carries <see cref="DaemonId"/> of the daemon that launched the process, private agents included.</summary>
    public const string DaemonIdVar = "KCAP_DAEMON_ID";

    /// <summary>Set to "1" on an envelope-sourced app-server session, whose transcript and approvals the
    /// daemon owns, so the Codex hook stands down from both.</summary>
    public const string HostedAppServerVar = "KCAP_HOSTED_APPSERVER";

    /// <summary>Set to "1" on a headless agent kcap itself runs, so its hooks answer the vendor but record nothing.</summary>
    public const string SkipVar = "KCAP_SKIP";

    /// <summary>The agent kcap's MCP servers ask the hosting daemon about, for the session they act for.
    /// Distinct from <see cref="AgentIdVar"/> because hooks read that one and link the session they
    /// post to the agent, and inside an ACP child the daemon already records the session itself.</summary>
    public const string DaemonAgentIdVar = "KCAP_DAEMON_AGENT_ID";

    /// <summary>The value <see cref="RenderedVar"/> carries when the web app renders the session.</summary>
    public const string Rendered = "1";

    /// <summary>Not hosted — what a terminal session gets.</summary>
    public static readonly HostedAgent Terminal = new(null, false, DaemonBridge.None);

    /// <summary>What <see cref="SessionKindVar"/> said. It selects the context a session is given and
    /// grants nothing: the flow agent id, which does carry authority, never reaches the agent process.</summary>
    public HostedSessionKind Kind { get; init; }

    /// <summary>The epoch of the daemon incarnation that launched this process: a resumed agent runs
    /// under a later one, so an end from this process is not the resumed run's end.</summary>
    public string? DaemonEpoch { get; init; }

    public bool IsHosted => AgentId is not null || Kind != HostedSessionKind.None;

    /// <summary>
    /// An exported variable can be empty, and a hook inherits its host's environment wholesale. An
    /// empty id is not an id: posted as <c>agent_host_id</c> it attributes the session to an agent
    /// nothing hosts. An id of blanks is still an id — nothing downstream reads it, so there is no
    /// call to guess at what was meant.
    /// </summary>
    public static HostedAgent FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(AgentIdVar) is { Length: > 0 } id ? id : null,
            Environment.GetEnvironmentVariable(RenderedVar) is Rendered,
            DaemonBridge.Parse(Environment.GetEnvironmentVariable(BridgeUrlVar))) {
            Kind        = HostedSessionKinds.Parse(Environment.GetEnvironmentVariable(SessionKindVar)),
            DaemonEpoch = Environment.GetEnvironmentVariable(DaemonEpochVar) is { Length: > 0 } epoch ? epoch : null
        };

    /// <summary>The markers a daemon launch puts into the agent's environment. The role is written
    /// only for a participant and only when the launch named one.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> LaunchMarkers(bool isFlowParticipant, string? flowRole) {
        var kind = isFlowParticipant ? HostedSessionKind.FlowParticipant : HostedSessionKind.HostedAgent;
        var markers = new List<KeyValuePair<string, string>> { new(SessionKindVar, kind.ToWire()!) };
        if (isFlowParticipant && !string.IsNullOrWhiteSpace(flowRole)) markers.Add(new(FlowRoleVar, flowRole));
        return markers;
    }

    /// <summary>A daemon's stable id: a hash of its name, so another daemon of the same user never matches.
    /// Persisted in surviving agents' environments, which a restarted daemon compares against, so these
    /// bytes must not change.</summary>
    public static string DaemonId(string? daemonName) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(daemonName ?? "")))
            [..16].ToLowerInvariant();

    /// <summary>What a kcap MCP server inside the agent needs to ask its daemon for the agent's session:
    /// the agent, and which of the daemons on the machine hosts it.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> DaemonLookupMarkers(string agentId, string daemonName) =>
        daemonName.Length == 0
            ? [new(DaemonAgentIdVar, agentId)]
            : [new(DaemonAgentIdVar, agentId), new(DaemonNameResolver.NameVar, daemonName)];
}

using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli;

/// <summary>The session each daemon-hosted agent is bound to, as its daemon answers. An agent's MCP
/// servers start before its session exists, so an empty answer is asked again on the next call; a
/// session, once known, is kept for the life of this process.</summary>
sealed class HostedAgentSessions(Func<string, string?> askDaemon) {
    /// <summary>Above the daemon's own wait for a launching agent to be bound, so its answer is not cut off.</summary>
    static readonly TimeSpan QueryBudget = TimeSpan.FromSeconds(15);

    /// <summary>Asks the daemon <c>KCAP_DAEMON_NAME</c> selects, resolved as every kcap command resolves
    /// it, in the daemons directory this process inherited.</summary>
    internal static readonly HostedAgentSessions Daemon = new(agentId =>
        AgentSessionIpc.QueryAsync(
                DaemonStore.FromEnvironment(), DaemonNameResolver.Resolve([]), agentId, TimeProvider.System, QueryBudget)
            .GetAwaiter().GetResult());

    readonly ConcurrentDictionary<string, string> _bound = new(StringComparer.Ordinal);

    public string? For(string agentId) {
        if (_bound.TryGetValue(agentId, out var known)) return known;

        return SessionIds.Canonical(askDaemon(agentId)) is { } session ? _bound.GetOrAdd(agentId, session) : null;
    }
}

using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Harness.Grok;

/// <summary>
/// Reads Grok's <c>subagent_spawned</c> and <c>subagent_finished</c>, which it sends for the parent
/// session outside <c>session/update</c>. The subagent's own updates then arrive as ordinary
/// <c>session/update</c>s under its <c>child_session_id</c>.
/// </summary>
internal static class GrokSubagentSignals {
    // session/load replays these under _x.ai/session/update; a replayed subagent is not a new one.
    const string LiveMethod = "_x.ai/session_notification";

    internal static AcpSubagentSignal? Read(AcpNotification notification) {
        if (notification.Method != LiveMethod
         || notification.Params?.Obj("update") is not { } update
         || update.Str("child_session_id") is not { Length: > 0 } child)
            return null;

        return update.Str("sessionUpdate") switch {
            "subagent_spawned"  => new AcpSubagentSignal(Started: true,  child, GrokSubagents.AgentId(child), update.Str("subagent_type")),
            "subagent_finished" => new AcpSubagentSignal(Started: false, child, GrokSubagents.AgentId(child), AgentType: null),
            _                   => null,
        };
    }
}

using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// Unsent composer text per session, held for the app's lifetime: a workspace is torn down on
/// every navigation away, so the draft has to outlive its chat tab.
public sealed class ComposerDrafts {
    readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    // The origin is part of the key: a local and a remote agent can share an id and are different agents.
    static string Key(AgentOrigin origin, string agentId) => $"{origin}:{agentId}";

    public string Get(AgentOrigin origin, string agentId) =>
        _drafts.TryGetValue(Key(origin, agentId), out var text) ? text : "";

    public void Set(AgentOrigin origin, string agentId, string text) {
        if (text.Length == 0) _drafts.Remove(Key(origin, agentId));
        else _drafts[Key(origin, agentId)] = text;
    }
}

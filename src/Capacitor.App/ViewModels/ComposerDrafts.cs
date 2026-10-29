using System.Reactive.Disposables;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// Unsent composer text per session, held for the app's lifetime: a workspace is torn down on
/// every navigation away, so the draft has to outlive its chat tab.
public sealed class ComposerDrafts {
    readonly Dictionary<string, (string Text, long Revision)> _drafts = new(StringComparer.Ordinal);
    readonly List<(string Key, Action<string> OnChanged)> _watchers = [];
    long _revision;

    // The origin is part of the key: a local and a remote agent can share an id and are different agents.
    static string Key(AgentOrigin origin, string agentId) => $"{origin}:{AgentIds.Normalize(agentId) ?? agentId}";

    public string Get(AgentOrigin origin, string agentId) => TextAt(Key(origin, agentId));

    /// Identifies one draft wherever it moves, until anyone types: a send that settles after its
    /// tab is gone clears its draft by this, never over a later edit. Zero for no draft.
    public long Revision(AgentOrigin origin, string agentId) =>
        _drafts.TryGetValue(Key(origin, agentId), out var draft) ? draft.Revision : 0;

    /// The composer's own write, so its watcher is not told of it.
    public void Set(AgentOrigin origin, string agentId, string text) {
        if (text.Length == 0) _drafts.Remove(Key(origin, agentId));
        else _drafts[Key(origin, agentId)] = (text, ++_revision);
    }

    /// Hands one session's draft to its other lane, keeping its revision.
    public void Move(AgentOrigin from, AgentOrigin to, string agentId) {
        var source = Key(from, agentId);
        var target = Key(to, agentId);
        if (_drafts.Remove(source, out var draft)) _drafts[target] = draft;
        else _drafts.Remove(target);
        Notify(source);
        Notify(target);
    }

    public void ClearDelivered(long revision) {
        if (revision == 0) return;
        foreach (var (key, draft) in _drafts) {
            if (draft.Revision != revision) continue;
            _drafts.Remove(key);
            Notify(key);
            return;
        }
    }

    /// Tells a live composer when its draft changes under it.
    public IDisposable Watch(AgentOrigin origin, string agentId, Action<string> onChanged) {
        var watcher = (Key(origin, agentId), onChanged);
        _watchers.Add(watcher);
        return Disposable.Create(() => _watchers.Remove(watcher));
    }

    string TextAt(string key) => _drafts.TryGetValue(key, out var draft) ? draft.Text : "";

    void Notify(string key) {
        foreach (var (watched, onChanged) in _watchers.ToList())
            if (watched == key) onChanged(TextAt(key));
    }
}

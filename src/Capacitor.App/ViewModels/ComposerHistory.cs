namespace Capacitor.App.ViewModels;

/// Prompts recalled with ↑/↓ the way a shell recalls commands: the input the transcript records
/// as submitted, followed by the sends it has not echoed yet. Recall starts only from a blank
/// composer and continues only while the box still shows the recalled text unchanged: an edit
/// turns it back into the user's own draft.
public sealed class ComposerHistory {
    public const int Capacity = 100;

    readonly List<string> _entries = new();
    // Where the shown entry stood. Arrivals, evictions and replays move entries between keystrokes,
    // so each step finds the shown text again from here instead of trusting the index.
    int _cursor;
    string? _shown;
    string _draft = "";

    /// Input the transcript records as submitted, in transcript order.
    public void Observe(string text) {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_entries.Count > 0 && string.Equals(_entries[^1], text, StringComparison.Ordinal)) return;
        _entries.Add(text);
        if (_entries.Count <= Capacity) return;
        _entries.RemoveAt(0);
        _cursor--;
    }

    /// The chat now reads another transcript.
    public void Clear() {
        _entries.Clear();
        EndNavigation();
    }

    /// The transcript is replaying from the start; a recall on screen picks up again once its
    /// turn is replayed.
    public void ClearForReplay() => _entries.Clear();

    /// The next older prompt, or null when there is none or `current` is a draft of the user's own.
    public string? Older(string current, IReadOnlyList<string> unechoed) {
        var all = All(unechoed);
        int target;
        if (IsShowing(current)) {
            target = Locate(all).Index - 1;
        } else {
            if (!string.IsNullOrWhiteSpace(current)) return null;
            _draft = current;
            target = all.Count - 1;
        }
        if (target < 0) return null;
        _cursor = target;
        return _shown = all[target];
    }

    /// The next newer prompt, the stashed draft once past the newest, or null outside navigation.
    public string? Newer(string current, IReadOnlyList<string> unechoed) {
        if (!IsShowing(current)) return null;
        var all = All(unechoed);
        var (index, found) = Locate(all);
        var target = found ? index + 1 : index;
        if (target < all.Count) {
            _cursor = target;
            return _shown = all[target];
        }
        EndNavigation();
        return _draft;
    }

    /// Leaves whatever the composer shows as the user's own draft.
    public void EndNavigation() => _shown = null;

    bool IsShowing(string current) => _shown is not null && string.Equals(_shown, current, StringComparison.Ordinal);

    List<string> All(IReadOnlyList<string> unechoed) {
        var all = new List<string>(_entries.Count + unechoed.Count);
        all.AddRange(_entries);
        all.AddRange(unechoed);
        return all;
    }

    /// The shown entry's index nearest where it stood; when it is gone, the slot it left, which
    /// is the index of the entry just newer than it.
    (int Index, bool Found) Locate(List<string> all) {
        var start = Math.Clamp(_cursor, 0, all.Count);
        for (var distance = 0; distance <= all.Count; distance++) {
            foreach (var i in (ReadOnlySpan<int>)[start - distance, start + distance]) {
                if (i >= 0 && i < all.Count && string.Equals(all[i], _shown, StringComparison.Ordinal)) return (i, true);
            }
        }
        return (start, false);
    }
}

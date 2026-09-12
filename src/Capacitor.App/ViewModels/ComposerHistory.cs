namespace Capacitor.App.ViewModels;

/// Prompts sent from one composer, recalled with ↑/↓ the way a shell recalls commands. Recall
/// starts only from a blank composer and continues only while the box still shows the recalled
/// text unchanged: an edit turns it back into the user's own draft.
public sealed class ComposerHistory {
    public const int Capacity = 100;

    readonly List<string> _entries = new();
    int _cursor;
    string? _shown;
    string _draft = "";

    public void Record(string text) {
        if (_entries.Count == 0 || !string.Equals(_entries[^1], text, StringComparison.Ordinal)) {
            _entries.Add(text);
            if (_entries.Count > Capacity) _entries.RemoveAt(0);
        }
        EndNavigation();
    }

    /// The next older prompt, or null when there is none or `current` is a draft of the user's own.
    public string? Older(string current) {
        if (!IsShowing(current)) {
            if (!string.IsNullOrWhiteSpace(current)) return null;
            _draft = current;
            _cursor = _entries.Count;
        }
        if (_cursor == 0) return null;
        _cursor--;
        return _shown = _entries[_cursor];
    }

    /// The next newer prompt, the stashed draft once past the newest, or null outside navigation.
    public string? Newer(string current) {
        if (!IsShowing(current)) return null;
        _cursor++;
        if (_cursor < _entries.Count) return _shown = _entries[_cursor];
        EndNavigation();
        return _draft;
    }

    bool IsShowing(string current) => _shown is not null && string.Equals(_shown, current, StringComparison.Ordinal);

    void EndNavigation() {
        _shown = null;
        _cursor = _entries.Count;
    }
}

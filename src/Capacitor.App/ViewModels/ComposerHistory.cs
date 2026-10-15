namespace Capacitor.App.ViewModels;

/// Prompts recalled with ↑/↓ the way a shell recalls commands: the transcript's user turns plus
/// what this composer sent. Recall starts only from a blank composer and continues only while the
/// box still shows the recalled text unchanged: an edit turns it back into the user's own draft.
public sealed class ComposerHistory {
    public const int Capacity = 100;

    readonly List<string> _entries = new();
    // The newest entries: sends whose transcript echo has not arrived. The transcript has not
    // shown them yet, so every turn it does show belongs ahead of them.
    int _pending;
    // While recalling: -1 once the shown entry is evicted from the front, Count once a clear has
    // detached it from the entries, so the replayed turns all sit above it.
    int _cursor;
    string? _shown;
    string _draft = "";

    /// A prompt this composer sent.
    public void Record(string text) {
        if (_entries.Count == 0 || !string.Equals(_entries[^1], text, StringComparison.Ordinal)) {
            _entries.Add(text);
            _pending++;
            TrimFront();
        }
        EndNavigation();
    }

    /// A user turn the transcript shows. A send's echo settles that send where it stands, along
    /// with any older send that will never echo; an arrival never interrupts a recall in progress.
    public void Observe(string text) {
        if (string.IsNullOrWhiteSpace(text)) return;
        var boundary = _entries.Count - _pending;
        var echoed = _entries.FindIndex(boundary, _pending, e => string.Equals(e, text, StringComparison.Ordinal));
        if (echoed >= 0) {
            _pending = _entries.Count - echoed - 1;
            return;
        }
        if (boundary > 0 && string.Equals(_entries[boundary - 1], text, StringComparison.Ordinal)) return;
        _entries.Insert(boundary, text);
        if (_shown is not null && boundary <= _cursor) _cursor++;
        TrimFront();
    }

    /// The transcript is being replayed from the start, so what it showed is about to arrive again;
    /// sends it has not echoed stay. A recall on screen stays one: ↓ still restores the draft.
    public void Clear() {
        _entries.RemoveRange(0, _entries.Count - _pending);
        _cursor = _entries.Count;
    }

    /// The next older prompt, or null when there is none or `current` is a draft of the user's own.
    public string? Older(string current) {
        if (!IsShowing(current)) {
            if (!string.IsNullOrWhiteSpace(current)) return null;
            _draft = current;
            _cursor = _entries.Count;
        }
        if (_cursor <= 0) return null;
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

    /// Leaves whatever the composer shows as the user's own draft.
    public void EndNavigation() {
        _shown = null;
        _cursor = _entries.Count;
    }

    void TrimFront() {
        if (_entries.Count <= Capacity) return;
        _entries.RemoveAt(0);
        _pending = Math.Min(_pending, _entries.Count);
        if (_shown is not null) _cursor = Math.Max(_cursor - 1, -1);
    }
}

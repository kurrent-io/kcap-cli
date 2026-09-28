using Avalonia.Collections;

namespace Capacitor.App.ViewModels;

/// Brings a bound list to the rows wanted without touching a row that stays: taking it out and
/// putting it back would rebuild its container and restart its pulse.
static class StableRows {
    public static void Sync<T>(AvaloniaList<T> shown, IEnumerable<T> wanted) where T : class {
        var rows = wanted.ToList();
        if (rows.SequenceEqual(shown)) return;

        for (var i = shown.Count - 1; i >= 0; i--)
            if (!rows.Contains(shown[i])) shown.RemoveAt(i);
        for (var i = 0; i < rows.Count; i++) {
            if (i < shown.Count && ReferenceEquals(shown[i], rows[i])) continue;
            var at = shown.IndexOf(rows[i]);
            if (at < 0) shown.Insert(i, rows[i]);
            else shown.Move(at, i);
        }
    }
}

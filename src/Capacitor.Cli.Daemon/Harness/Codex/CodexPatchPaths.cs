using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>The paths each file-change item touches, as the app-server announced them. A file-change approval names
/// no paths of its own, so until its item's paths are known no path rule can be checked against it.</summary>
internal sealed class CodexPatchPaths {
    const int MaxItems = 64;

    readonly Lock _gate = new();
    readonly Dictionary<string, IReadOnlyList<string>> _paths = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    /// <summary>From <c>item/fileChange/patchUpdated</c> (<c>params.changes</c>) or a started file-change item
    /// (<c>item.changes</c>): each change names its <c>path</c>. A later snapshot replaces an earlier one.</summary>
    public void Observe(string itemId, JsonElement changes) {
        if (!changes.IsArray) return;

        var paths = changes.EnumerateArray()
            .Select(c => c.Str("path"))
            .OfType<string>()
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0) return;

        lock (_gate) {
            if (!_paths.ContainsKey(itemId)) _order.Enqueue(itemId);
            _paths[itemId] = paths;
            if (_order.Count > MaxItems) _paths.Remove(_order.Dequeue());
        }
    }

    public bool TryPaths(string itemId, out IReadOnlyList<string> paths) {
        lock (_gate) return _paths.TryGetValue(itemId, out paths!);
    }
}

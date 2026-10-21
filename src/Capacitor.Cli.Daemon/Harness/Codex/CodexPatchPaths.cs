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
    /// (<c>item.changes</c>): each change names its <c>path</c>, and a rename its destination in <c>kind.move_path</c>.
    /// A later snapshot replaces an earlier one; one with a change naming no path leaves the item's paths unknown.</summary>
    public void Observe(string itemId, JsonElement changes) {
        if (!changes.IsArray) return;

        var paths = new List<string>();
        foreach (var change in changes.EnumerateArray()) {
            if (change.Str("path") is not { Length: > 0 } path) {
                lock (_gate) _paths.Remove(itemId);
                return;
            }

            paths.Add(path);
            if (change.Obj("kind") is { } kind && kind.Str("move_path") is { Length: > 0 } destination) paths.Add(destination);
        }
        if (paths.Count == 0) return;

        lock (_gate) {
            if (!_paths.ContainsKey(itemId)) _order.Enqueue(itemId);
            _paths[itemId] = [.. paths.Distinct(StringComparer.Ordinal)];
            if (_order.Count > MaxItems) _paths.Remove(_order.Dequeue());
        }
    }

    public bool TryPaths(string itemId, out IReadOnlyList<string> paths) {
        lock (_gate) return _paths.TryGetValue(itemId, out paths!);
    }
}

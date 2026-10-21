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
    /// A later snapshot replaces an earlier one; one that is empty or not an array, or with a change naming no path or
    /// a destination that is not a string, leaves the item's paths unknown. A destination is kept even when empty, so it
    /// fails to resolve rather than going unchecked.</summary>
    public void Observe(string itemId, JsonElement changes) {
        var paths = changes.IsArray ? PathsOf(changes) : null;

        lock (_gate) {
            if (!_paths.ContainsKey(itemId)) _order.Enqueue(itemId);
            _paths[itemId] = paths is null ? [] : [.. paths.Distinct(StringComparer.Ordinal)];
            if (_order.Count > MaxItems) _paths.Remove(_order.Dequeue());
        }
    }

    /// <summary>Every path the changes touch, or null when any change is shaped so that one could be missed.</summary>
    static List<string>? PathsOf(JsonElement changes) {
        var paths = new List<string>();
        foreach (var change in changes.EnumerateArray()) {
            if (change.Str("path") is not { Length: > 0 } path) return null;
            paths.Add(path);

            if (change.Obj("kind") is not { } kind || !kind.TryGetProperty("move_path", out var move)) continue;
            if (move.ValueKind == JsonValueKind.String) paths.Add(move.GetString()!);
            else if (move.ValueKind != JsonValueKind.Null) return null;
        }
        return paths;
    }

    /// <summary>False until the item's latest snapshot names every path it touches.</summary>
    public bool TryPaths(string itemId, out IReadOnlyList<string> paths) {
        lock (_gate) return _paths.TryGetValue(itemId, out paths!) && paths.Count > 0;
    }
}

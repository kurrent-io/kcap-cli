namespace Capacitor.Cli.Core.Skills;

/// <summary>
/// The rows one run works over, keyed by physical path. Path equality is canonical and fails
/// closed: a row whose path cannot be fully resolved, a second row resolving onto a first, and a
/// row the validator refuses are all held aside — carried into every save untouched, and acted on
/// by nothing.
/// </summary>
public sealed class OwnedSkillRows {
    readonly Dictionary<string, OwnedSkillRow> _live = [];
    readonly List<string>                      _order = [];
    readonly List<OwnedSkillRow>               _asideRows = [];

    /// <summary>Adopts a ledger's rows, reporting each one held aside and why.</summary>
    public static OwnedSkillRows Adopt(SkillsLedger? ledger, Action<OwnedSkillRow, string> refuse) {
        var rows = new OwnedSkillRows();

        foreach (var row in ledger?.Rows ?? []) {
            if (SkillsLedgerValidation.Reject(row) is { } reason) { rows.SetAside(row, refuse, reason); continue; }
            if (!CanonicalPath.TryResolve(row.Path, out var resolved)) {
                rows.SetAside(row, refuse, "its path cannot be resolved");
                continue;
            }
            var key = PathComparison.Key(resolved);
            if (!rows._live.TryAdd(key, row)) {
                rows.SetAside(row, refuse, "another row already owns that location");
                continue;
            }
            rows._order.Add(key);
        }

        return rows;
    }

    /// <summary>Rows acted on this run, in the order the ledger recorded them.</summary>
    public IEnumerable<OwnedSkillRow> Live => _order.Select(k => _live[k]);

    /// <summary>Every row the next save writes: the live ones and the ones held aside.</summary>
    public OwnedSkillRow[] All => [.. Live, .. _asideRows];

    public OwnedSkillRow? At(string path) =>
        Entry(path) is { } key && _live.TryGetValue(key, out var row) ? row : null;

    /// <summary>Records a row at its own path, replacing whatever that location held.</summary>
    public void Put(OwnedSkillRow row) {
        if (Key(row.Path) is not { } key) return;
        if (_live.TryAdd(key, row)) _order.Add(key); else _live[key] = row;
    }

    public void Remove(string path) {
        if (Entry(path) is not { } key || !_live.Remove(key)) return;
        _order.Remove(key);
    }

    /// <summary>The entry this path was admitted as. A key is resolved through the filesystem, and
    /// the filesystem changes underneath a run that deletes: a destination reached through a link
    /// resolves to its target while it stands and to itself once it is gone. So a row is found by
    /// the path it records first, and only then by where that path currently leads — a key
    /// recomputed after the mutation finds nothing, and the row outlives the deletion that ended
    /// it.</summary>
    string? Entry(string path) =>
        _order.FirstOrDefault(key => PathComparison.Equal(_live[key].Path, path)) ?? Key(path);

    /// <summary>Moves a row to the location it was found at, keeping nothing behind at the old
    /// one.</summary>
    public void Relocate(string from, OwnedSkillRow moved) {
        if (Key(from) is { } was && Key(moved.Path) is { } now && was == now) { Put(moved); return; }

        Remove(from);
        Put(moved);
    }

    static string? Key(string path) =>
        CanonicalPath.TryResolve(path, out var resolved) ? PathComparison.Key(resolved) : null;

    void SetAside(OwnedSkillRow row, Action<OwnedSkillRow, string> refuse, string reason) {
        _asideRows.Add(row);
        refuse(row, reason);
    }
}

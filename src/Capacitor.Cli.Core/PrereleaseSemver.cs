namespace Capacitor.Cli.Core;

/// <summary>
/// SemVer 2.0 precedence comparator that ORDERS prereleases
/// (0.7.0-beta.1 &lt; 0.7.0-beta.2 &lt; 0.7.0). Build metadata (+…) is ignored.
/// Unlike <see cref="SemverCompare"/> (which strips -prerelease and cannot order
/// betas), this is required by the opt-in beta update channel. Null / empty /
/// "unknown" / unparseable inputs sort lowest, and <see cref="IsNewer"/> returns
/// false when either side is unparseable ("don't know → don't claim newer").
/// </summary>
public static class PrereleaseSemver {
    public static bool IsNewer(string? candidate, string? current) {
        var c   = Parse(candidate);
        var cur = Parse(current);
        if (c is null || cur is null) return false;
        return CompareParsed(c.Value, cur.Value) > 0;
    }

    public static int Compare(string? a, string? b) {
        var pa = Parse(a);
        var pb = Parse(b);
        if (pa is null && pb is null) return 0;
        if (pa is null) return -1;
        if (pb is null) return 1;
        return CompareParsed(pa.Value, pb.Value);
    }

    readonly record struct V(int Major, int Minor, int Patch, string[] Pre);

    static V? Parse(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (string.Equals(s, "unknown", StringComparison.Ordinal)) return null;

        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];                 // drop build metadata

        string core;
        string[] pre;
        var dash = s.IndexOf('-');
        if (dash >= 0) {
            core = s[..dash];
            var preStr = s[(dash + 1)..];
            if (preStr.Length == 0) return null;
            pre = preStr.Split('.');
            foreach (var id in pre) if (id.Length == 0) return null;
        } else {
            core = s;
            pre  = [];                                 // Array.Empty<string>() — AOT-safe
        }

        var parts = core.Split('.');
        if (parts.Length != 3) return null;
        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch)) return null;
        if (major < 0 || minor < 0 || patch < 0) return null;

        return new V(major, minor, patch, pre);
    }

    static int CompareParsed(V a, V b) {
        var c = a.Major.CompareTo(b.Major); if (c != 0) return c;
        c     = a.Minor.CompareTo(b.Minor); if (c != 0) return c;
        c     = a.Patch.CompareTo(b.Patch); if (c != 0) return c;

        var aPre = a.Pre.Length > 0;
        var bPre = b.Pre.Length > 0;
        if (!aPre && !bPre) return 0;
        if (aPre && !bPre)  return -1;                 // 0.7.0-beta.1 < 0.7.0
        if (!aPre && bPre)  return 1;

        var len = Math.Min(a.Pre.Length, b.Pre.Length);
        for (var i = 0; i < len; i++) {
            var ai = a.Pre[i];
            var bi = b.Pre[i];
            var aNum = int.TryParse(ai, out var an);
            var bNum = int.TryParse(bi, out var bn);
            if (aNum && bNum) {
                var nc = an.CompareTo(bn); if (nc != 0) return nc;
            } else if (aNum) {
                return -1;                             // numeric < alphanumeric
            } else if (bNum) {
                return 1;
            } else {
                var sc = string.CompareOrdinal(ai, bi);
                if (sc != 0) return sc < 0 ? -1 : 1;
            }
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);   // more identifiers wins
    }
}

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The cite-handle grammar and verdict expansion.</summary>
public static class JudgeCiteHandles {
    public const int MaxHandleBytes = 16;

    public static string Page(int n)             => $"p{n}";
    public static string Seeded(int n)           => $"o{n}";
    public static string Row(string page, int k) => $"{page}.{k}";
    public static string OneShot(int k)          => $"e{k}";

    public static IReadOnlyList<string> Expand(IEnumerable<string> tokens, JudgeLedger ledger, int max, out int dropped) {
        var refs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        dropped = 0;
        foreach (var token in tokens) {
            if (!seenTokens.Add(token)) continue;
            if (!ledger.TryExpand(token, out var canonical)) { dropped++; continue; }
            if (!seen.Add(canonical)) continue;
            if (refs.Count >= max) { dropped++; continue; }
            refs.Add(canonical);
        }
        return refs;
    }
}

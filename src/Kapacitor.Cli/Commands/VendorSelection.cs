namespace Kapacitor.Cli.Commands;

/// <summary>
/// Pure parser that resolves the set of selected import sources from args.
/// Empty vendor set = "all detected" (orchestrator handles availability).
/// Returns a structured Error for vendor-flag typos and source-option misuse;
/// leaves all other --flags untouched so unrelated globals (--server-url,
/// --no-update-check, --profile, ...) flow through dispatch unchanged.
/// </summary>
public static class VendorSelection {
    public sealed record Result(IReadOnlySet<string> Vendors, string? Error) {
        public bool HasError => Error is not null;
    }

    static readonly string[] KnownVendorFlags = ["--claude", "--codex", "--cursor"];
    static readonly string[] KnownSourceOptionFlags = ["--cursor-workspace", "--cursor-all-workspaces"];

    public static Result Parse(string[] args) {
        var vendors = new HashSet<string>(StringComparer.Ordinal);
        var sawCursorWorkspace = false;
        var sawCursorAllWorkspaces = false;

        // First pass: collect vendors + source-options, consume the value after --cursor-workspace.
        for (var i = 0; i < args.Length; i++) {
            var a = args[i];
            switch (a) {
                case "--claude": vendors.Add("claude"); break;
                case "--codex":  vendors.Add("codex");  break;
                case "--cursor": vendors.Add("cursor"); break;
                case "--cursor-workspace":
                    sawCursorWorkspace = true;
                    vendors.Add("cursor");
                    // Only consume the next token if it doesn't look like another flag.
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) i++;
                    break;
                case "--cursor-all-workspaces":
                    sawCursorAllWorkspaces = true;
                    vendors.Add("cursor");
                    break;
            }
        }

        if (sawCursorWorkspace && sawCursorAllWorkspaces) {
            return new(vendors, "--cursor-workspace and --cursor-all-workspaces are mutually exclusive.");
        }

        // Second pass: unknown vendor-prefix flag rejection.
        for (var i = 0; i < args.Length; i++) {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            if (i > 0 && args[i - 1] == "--cursor-workspace") continue; // skip the value

            var isKnownVendor = Array.IndexOf(KnownVendorFlags, a) >= 0;
            var isKnownOption = Array.IndexOf(KnownSourceOptionFlags, a) >= 0;
            if (isKnownVendor || isKnownOption) continue;

            if (a.StartsWith("--cursor-") || a.StartsWith("--claude-") || a.StartsWith("--codex-")) {
                var hint = FindClosest(a, KnownSourceOptionFlags, maxDistance: 3);
                return new(vendors, hint is null
                    ? $"Unknown source option: {a}."
                    : $"Unknown source option: {a}. Did you mean {hint}?");
            }
        }

        // Third pass: vendor-typo detection (Damerau-Levenshtein <= 2 against vendor flags).
        for (var i = 0; i < args.Length; i++) {
            var a = args[i];
            if (!a.StartsWith("--")) continue;
            if (i > 0 && args[i - 1] == "--cursor-workspace") continue;
            if (Array.IndexOf(KnownVendorFlags, a) >= 0) continue;
            if (Array.IndexOf(KnownSourceOptionFlags, a) >= 0) continue;
            if (a.StartsWith("--cursor-") || a.StartsWith("--claude-") || a.StartsWith("--codex-")) continue;

            string? hint = null;
            var bestDist = int.MaxValue;
            foreach (var v in KnownVendorFlags) {
                var d = DamerauLevenshtein(a, v);
                if (d < bestDist) { bestDist = d; hint = v; }
            }
            if (bestDist is > 0 and <= 2) {
                return new(vendors, $"Unknown vendor flag: {a}. Did you mean {hint}?");
            }
        }

        return new(vendors, null);
    }

    static string? FindClosest(string input, IReadOnlyList<string> candidates, int maxDistance) {
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (var c in candidates) {
            var d = DamerauLevenshtein(input, c);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return bestDist <= maxDistance ? best : null;
    }

    static int DamerauLevenshtein(string a, string b) {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++) {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
            }
        return d[a.Length, b.Length];
    }
}

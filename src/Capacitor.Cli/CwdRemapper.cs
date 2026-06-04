using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>
/// Applies user-configured path-prefix remaps to a transcript cwd so historic
/// sessions that recorded a since-renamed local repo path (e.g.
/// <c>~/dev/kapacitor-cli</c> after rename to <c>~/dev/kcap-cli</c>) can still
/// be matched to an on-disk git repository during <c>kcap import</c>.
///
/// Matching rules:
/// - <c>~</c> or <c>~/</c> at the start of <c>from</c>/<c>to</c> is expanded to
///   the current user's home directory (transcript cwds are absolute).
/// - Path-boundary prefix: <c>cwd == from</c> or <c>cwd starts with from + "/"</c>,
///   so <c>/dev/kapacitor</c> does NOT spuriously match <c>/dev/kapacitor-cli</c>.
/// - Longest <c>from</c> wins when multiple rules could apply.
/// - At most one remap is applied (no chaining), to keep behavior predictable.
/// </summary>
static class CwdRemapper {
    public static string Apply(string cwd, IReadOnlyList<CwdRemap>? rules) =>
        Apply(cwd, rules, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // Internal seam so tests can pin the home directory and stay cross-platform.
    internal static string Apply(string cwd, IReadOnlyList<CwdRemap>? rules, string home) {
        if (rules is null or { Count: 0 } || string.IsNullOrEmpty(cwd)) return cwd;

        CwdRemap? best       = null;
        string?   bestFrom   = null;
        string?   bestTo     = null;
        var       bestLen    = -1;

        foreach (var rule in rules) {
            if (string.IsNullOrEmpty(rule.From) || rule.To is null) continue;

            var from = ExpandHome(rule.From, home);

            if (!IsPrefixMatch(cwd, from)) continue;

            if (from.Length > bestLen) {
                best     = rule;
                bestFrom = from;
                bestTo   = ExpandHome(rule.To, home);
                bestLen  = from.Length;
            }
        }

        if (best is null) return cwd;

        // cwd == from → use 'to' verbatim; otherwise replace prefix + keep the
        // tail (the leading '/' of the tail was at position from.Length).
        return cwd.Length == bestFrom!.Length
            ? bestTo!
            : bestTo + cwd[bestFrom.Length..];
    }

    static string ExpandHome(string path, string home) {
        if (path.Length == 0 || path[0] != '~') return path;
        if (path.Length == 1) return home;                    // "~"
        if (path[1] == '/') return home + path[1..];          // "~/foo"
        return path;                                          // "~user" / "~foo" — leave alone
    }

    static bool IsPrefixMatch(string cwd, string from) {
        if (!cwd.StartsWith(from, StringComparison.Ordinal)) return false;
        if (cwd.Length == from.Length) return true;
        return cwd[from.Length] == '/';
    }
}

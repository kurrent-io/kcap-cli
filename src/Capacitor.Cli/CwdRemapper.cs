using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>
/// Applies user-configured path remaps to a transcript cwd so historic
/// sessions that recorded a since-renamed local repo path (e.g.
/// <c>~/dev/kapacitor-cli</c> after rename to <c>~/dev/kcap-cli</c>) can still
/// be matched to an on-disk git repository during <c>kcap import</c>.
///
/// Matching rules:
/// - <c>~</c> or <c>~/</c> (or <c>~\</c> on Windows) at the start of
///   <c>from</c>/<c>to</c> is expanded to the given home directory
///   (transcript cwds are absolute).
/// - Path-boundary prefix: <c>cwd == from</c> or <c>cwd starts with from + sep</c>
///   where <c>sep</c> is either <c>/</c> or <c>\</c>; so <c>/dev/kapacitor</c>
///   does NOT spuriously match <c>/dev/kapacitor-cli</c>.
/// - <c>from</c> may carry one <c>*</c> standing as a whole segment, which
///   matches exactly one segment of the cwd — <c>~/dev/repo/worktrees/*</c>
///   covers every worktree under that directory.
/// - Comparisons are case-insensitive on Windows, case-sensitive elsewhere —
///   matching the host filesystem's behavior.
/// - The rule consuming the most of the cwd wins; a literal <c>from</c> beats a
///   wildcard one that consumed the same amount, so a single path can be
///   overridden out of a family.
/// - At most one remap is applied (no chaining), to keep behavior predictable.
/// </summary>
static class CwdRemapper {
    static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string Apply(string cwd, IReadOnlyList<CwdRemap>? rules, UserHome home) =>
        Apply(cwd, rules, home.Path, PathComparison);

    // Internal seam for tests that need to pin the comparison policy (so the
    // case-sensitivity behavior can be exercised regardless of host OS).
    internal static string Apply(string cwd, IReadOnlyList<CwdRemap>? rules, string home, StringComparison comparison) {
        if (rules is null or { Count: 0 } || string.IsNullOrEmpty(cwd)) return cwd;

        string? bestTo      = null;
        var     bestLen     = -1;
        var     bestLiteral = false;

        foreach (var rule in rules) {
            if (string.IsNullOrEmpty(rule.From) || rule.To is null) continue;
            if (!TryParseFrom(ExpandHome(rule.From, home), out var pattern, out _)) continue;

            var matched = pattern.Match(cwd, comparison);

            if (matched < 0) continue;

            var literal = !pattern.HasWildcard;

            // Same reach: the literal rule is the more specific statement, so it
            // stays usable as an override for one path inside a wildcard family.
            var better = matched > bestLen || (matched == bestLen && literal && !bestLiteral);

            if (!better) continue;

            bestTo      = ExpandHome(rule.To, home);
            bestLen     = matched;
            bestLiteral = literal;
        }

        if (bestTo is null) return cwd;

        return bestLen == cwd.Length ? bestTo : bestTo + cwd[bestLen..];
    }

    /// <summary>
    /// A <c>from</c> pattern split around its wildcard: <see cref="Head"/> is the
    /// literal text before the <c>*</c> and <see cref="Tail"/> the literal text
    /// after it, or <c>null</c> when the pattern carries no wildcard.
    /// </summary>
    internal readonly record struct FromPattern(string Head, string? Tail) {
        public bool HasWildcard => Tail is not null;

        /// <summary>
        /// How many characters of <paramref name="cwd"/> this pattern consumes,
        /// or -1 when it does not match. The caller keeps the remainder.
        /// </summary>
        public int Match(string cwd, StringComparison comparison) {
            if (Tail is null) return IsPrefixMatch(cwd, Head, comparison) ? Head.Length : -1;
            if (!cwd.StartsWith(Head, comparison)) return -1;

            var segEnd = Head.Length;
            while (segEnd < cwd.Length && !IsSeparator(cwd[segEnd])) segEnd++;

            if (segEnd == Head.Length) return -1;             // the wildcard matches one segment, never none
            if (Tail.Length == 0) return segEnd;

            var end = segEnd + Tail.Length;

            if (end > cwd.Length) return -1;
            if (!cwd.AsSpan(segEnd, Tail.Length).Equals(Tail, comparison)) return -1;

            return end == cwd.Length || IsSeparator(cwd[end]) ? end : -1;
        }
    }

    /// <summary>
    /// Parse a <c>from</c> into <see cref="FromPattern"/>, rejecting a wildcard
    /// that does not stand as a whole segment. <c>error</c> is a user-facing
    /// sentence and is only set when parsing fails.
    /// </summary>
    internal static bool TryParseFrom(string from, out FromPattern pattern, out string error) {
        pattern = new(from, null);
        error   = "";

        var star = from.IndexOf('*');

        if (star < 0) return true;

        if (from.IndexOf('*', star + 1) >= 0) {
            error = "only one '*' is supported, and it matches a single path segment";

            return false;
        }

        var head = from[..star];
        var tail = from[(star + 1)..];

        if (head.Length == 0 || !IsSeparator(head[^1]) || (tail.Length > 0 && !IsSeparator(tail[0]))) {
            error = "'*' must stand alone as a path segment, e.g. ~/dev/repo/worktrees/*";

            return false;
        }

        pattern = new(head, tail);

        return true;
    }

    static string ExpandHome(string path, string home) {
        if (path.Length == 0 || path[0] != '~') return path;
        if (path.Length == 1) return home;                  // "~"
        if (IsSeparator(path[1])) return home + path[1..];  // "~/foo" or "~\foo"
        return path;                                        // "~user" / "~foo" — leave alone
    }

    static bool IsPrefixMatch(string cwd, string from, StringComparison comparison) {
        if (!cwd.StartsWith(from, comparison)) return false;
        if (cwd.Length == from.Length) return true;
        return IsSeparator(cwd[from.Length]);
    }

    internal static bool IsSeparator(char c) => c == '/' || c == '\\';
}

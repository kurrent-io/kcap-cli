using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Auth;

public readonly record struct SlugCheck(bool Ok, string? Reason);

// Mirrors kcap-web/src/server/tenants/slug.ts. The server re-validates; this is
// for instant CLI feedback before any network call.
public static partial class SlugValidator {
    // Same charset rule as SLUG_PATTERN: lowercase DNS label, <=40, no leading/
    // trailing/double hyphen. [GeneratedRegex] keeps it AOT-safe.
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,39}$")]
    private static partial Regex SlugRegex();

    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) {
        "www", "auth", "api", "admin", "app", "dashboard", "status", "static",
        "cdn", "mail", "kcap", "kurrent", "capacitor", "internal", "support",
        "help", "docs", "blog", "assets", "console",
    };

    public static string Canonicalize(string input) => input.Trim().ToLowerInvariant();

    public static SlugCheck Validate(string canonical) {
        if (!SlugRegex().IsMatch(canonical)) return new(false, "invalid");
        if (Reserved.Contains(canonical))    return new(false, "blocked");
        return new(true, null);
    }

    // Best-effort default slug from an org name: strip diacritics, lowercase,
    // non-alphanumeric -> '-', collapse repeats, trim hyphens, cap at 40.
    public static string Derive(string orgName) {
        var decomposed = orgName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed) {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        var lowered = sb.ToString();

        var outSb = new StringBuilder(lowered.Length);
        var pendingHyphen = false;
        foreach (var ch in lowered) {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') {
                if (pendingHyphen && outSb.Length > 0) outSb.Append('-');
                pendingHyphen = false;
                outSb.Append(ch);
            } else {
                pendingHyphen = true; // collapse any run of non-alphanumerics into one hyphen
            }
        }
        var slug = outSb.ToString();
        return slug.Length > 40 ? slug[..40] : slug;
    }
}

using System.Text;

namespace Capacitor.Cli.Commands;

/// <summary>The server's memory-slug normalisation, applied before a save so a plan shows the slug the server will
/// store: lowercase <c>[a-z0-9]</c>, runs of space, <c>_</c> and <c>-</c> folded to one <c>-</c>, everything else
/// dropped. Must stay in step with the server's <c>SlugRules</c>, or a re-run reports a stored slug as new.</summary>
public static class MemorySlug {
    public const int MaxLength = 64;

    /// <summary>The normalised slug, or null when nothing of 1-64 chars remains.</summary>
    public static string? Normalize(string input) {
        var sb = new StringBuilder(input.Length);

        foreach (var ch in input.Trim().ToLowerInvariant()) {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (ch is ' ' or '_' or '-' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var slug = sb.ToString().Trim('-');

        return slug.Length is 0 or > MaxLength ? null : slug;
    }
}

using System.Text;

namespace Capacitor.Cli.Core.Skills;

/// <summary>What a snapshot row becomes on disk, and which slugs may reach a filesystem operation
/// at all.</summary>
public static class SkillsRendering {
    /// <summary>The materialized SKILL.md: YAML frontmatter (name + when-to-use description as a
    /// double-quoted scalar) over the approved body.</summary>
    public static string RenderSkillFile(SkillSnapshotItem item) {
        var sb = new StringBuilder();
        sb.Append("---\n");
        sb.Append("name: ").Append(item.Slug).Append('\n');
        var description = string.IsNullOrWhiteSpace(item.Description) ? item.Title : item.Description!;
        sb.Append("description: ").Append(YamlQuote(description)).Append('\n');
        sb.Append("---\n\n");
        sb.Append(item.Body);
        if (!item.Body.EndsWith('\n')) sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>A slug usable as a single path segment: lowercase alphanumerics and dashes only —
    /// exactly the server's slug alphabet. Anything else (separators, dots, empty) is refused
    /// before it can reach a filesystem operation.</summary>
    public static bool IsSafeSlug(string slug) =>
        slug.Length is > 0 and <= 100 && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    static string YamlQuote(string value) {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
            sb.Append(c switch {
                '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                _ => c.ToString(),
            });
        return sb.Append('"').ToString();
    }
}

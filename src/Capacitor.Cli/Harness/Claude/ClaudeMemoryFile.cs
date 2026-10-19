namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// One note from Claude Code's auto-memory folder (<c>~/.claude/projects/&lt;slug&gt;/memory/*.md</c>): YAML frontmatter
/// carrying <c>name</c>, <c>description</c> and a type, then a markdown body. The type sits either at the top level or
/// under <c>metadata:</c>, and both shapes are on disk.
/// </summary>
public sealed record ClaudeMemoryFile(string FileName, string? Name, string? Description, string? Type, string Body) {
    /// <summary>The folder's index, which Claude rebuilds from the notes; it is not a note itself.</summary>
    public const string IndexFileName = "MEMORY.md";

    /// <summary>Reads only the flat <c>key: value</c> pairs the notes use, plus one nested <c>metadata:</c> block and
    /// quoted or block-scalar values. A file without frontmatter is all body.</summary>
    public static ClaudeMemoryFile Parse(string fileName, string text) {
        var lines = text.TrimStart('\uFEFF').ReplaceLineEndings("\n").Split('\n');

        if (lines.Length == 0 || lines[0].TrimEnd() != "---") return new(fileName, null, null, null, text);

        var close = Array.FindIndex(lines, 1, l => l.TrimEnd() == "---");
        if (close < 0) return new(fileName, null, null, null, text);

        var fields = ReadFields(lines[1..close]);
        var body   = string.Join('\n', lines[(close + 1)..]);

        return new(
            fileName,
            fields.GetValueOrDefault("name"),
            fields.GetValueOrDefault("description"),
            fields.GetValueOrDefault("metadata.type") ?? fields.GetValueOrDefault("type"),
            body);
    }

    static Dictionary<string, string> ReadFields(string[] lines) {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? block = null;

        for (var i = 0; i < lines.Length; i++) {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;

            var indent = line.Length - line.TrimStart().Length;
            if (indent == 0) block = null;

            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var key   = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (indent == 0 && value.Length == 0) {
                block = key;
                continue;
            }

            if (value is ">" or ">-" or "|" or "|-") {
                var folded = value[0] == '>';
                var parts  = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].Trim().Length == 0 || Indent(lines[i + 1]) > indent)) parts.Add(lines[++i].Trim());
                value = string.Join(folded ? " " : "\n", parts).Trim();
            } else {
                value = Unquote(value);
            }

            fields[indent > 0 && block is not null ? $"{block}.{key}" : key] = value;
        }

        return fields;
    }

    static int Indent(string line) => line.Length - line.TrimStart().Length;

    static string Unquote(string value) {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");

        return value;
    }
}

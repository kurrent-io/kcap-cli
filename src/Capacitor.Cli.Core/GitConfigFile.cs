namespace Capacitor.Cli.Core;

/// <summary>
/// Reads one variable straight from a git config file, with no git process. Enough of the format for
/// a plain <c>[section]</c> variable: subsections, <c>include</c> and line continuations are not
/// followed, so a value set only through those reads as unset.
/// </summary>
public static class GitConfigFile {
    /// <summary>The last value <paramref name="section"/>.<paramref name="name"/> takes in the file, as
    /// git would resolve it there; a bare name with no <c>=</c> is <c>true</c>. Null when the file or the
    /// variable is absent or unreadable.</summary>
    public static string? Get(string path, string section, string name) {
        string[] lines;
        try {
            if (!File.Exists(path)) return null;
            lines = File.ReadAllLines(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }

        string? value = null;
        var inSection = false;
        foreach (var raw in lines) {
            var line = raw.Trim();
            if (line.StartsWith('[')) {
                var close = line.IndexOf(']');
                if (close < 0) continue;
                inSection = line[1..close].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                line = line[(close + 1)..].Trim();
            }
            if (!inSection || line.Length == 0 || line[0] is '#' or ';') continue;

            var eq = line.IndexOf('=');
            var key = (eq < 0 ? line : line[..eq]).Trim();
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;

            value = eq < 0 ? "true" : Unquote(line[(eq + 1)..]);
        }

        return value;
    }

    /// <summary>Git's boolean spelling: <c>true</c>, <c>yes</c>, <c>on</c> or a non-zero integer.</summary>
    public static bool IsTrue(string? value) =>
        value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase)
                           || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                           || (int.TryParse(value, out var n) && n != 0));

    static string Unquote(string raw) {
        var result = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in raw.Trim()) {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && c is '#' or ';') break;
            result.Append(c);
        }

        return result.ToString().Trim();
    }
}

using System.Text;

namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>
/// Filesystem layout for Grok Build: everything under <c>$GROK_HOME</c> when set, otherwise
/// <c>~/.grok</c>. Grok merges every <c>*.json</c> in <see cref="HooksDir"/>, so kcap owns
/// <see cref="KcapHooksJson"/> outright.
/// </summary>
public sealed class GrokPaths {
    /// <summary>Longest directory name Grok writes before swapping the encoded cwd for a slug.</summary>
    const int MaxDirNameBytes = 255;

    public GrokPaths(UserHome home, string? grokHome) =>
        Home = !string.IsNullOrWhiteSpace(grokHome) ? grokHome : Path.Combine(home.Path, ".grok");

    public string Home { get; }

    public string HooksDir => Path.Combine(Home, "hooks");

    public string KcapHooksJson => Path.Combine(HooksDir, "kcap.json");

    public string ConfigToml => Path.Combine(Home, "config.toml");

    /// <summary>One directory per cwd, holding one directory per session.</summary>
    public string SessionsDir => Path.Combine(Home, "sessions");

    /// <summary>
    /// The session's <c>updates.jsonl</c>, or null when its directory cannot be named. The directory
    /// is the cwd percent-encoded; past <see cref="MaxDirNameBytes"/> Grok uses a slug instead and
    /// records the real cwd in that directory's <c>.cwd</c> file.
    /// </summary>
    public string? UpdatesJsonl(string cwd, string sessionId) {
        var encoded = Uri.EscapeDataString(cwd);

        if (Encoding.UTF8.GetByteCount(encoded) <= MaxDirNameBytes)
            return Path.Combine(SessionsDir, encoded, sessionId, "updates.jsonl");

        return SlugDirFor(cwd) is { } dir ? Path.Combine(dir, sessionId, "updates.jsonl") : null;
    }

    string? SlugDirFor(string cwd) {
        if (!Directory.Exists(SessionsDir)) return null;

        foreach (var dir in Directory.EnumerateDirectories(SessionsDir)) {
            var marker = Path.Combine(dir, ".cwd");

            try {
                if (File.Exists(marker) && File.ReadAllText(marker).TrimEnd('\n', '\r') == cwd) return dir;
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    /// <summary>Whether Grok has run here. kcap can create <see cref="HooksDir"/> first, so the root
    /// alone does not count.</summary>
    public bool HasUserData() =>
        Directory.Exists(Home) && (File.Exists(ConfigToml) || Directory.Exists(SessionsDir));
}

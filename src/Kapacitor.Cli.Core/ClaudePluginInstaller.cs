using System.Text.Json.Nodes;

namespace Kapacitor.Cli.Core;

/// <summary>
/// Marker + detection helpers for the Claude Code settings file
/// (<c>~/.claude/settings.json</c> for user scope, or
/// <c>&lt;repo&gt;/.claude/settings.local.json</c> for project scope).
/// Mirrors <see cref="AgentsSkillsInstaller"/> and
/// <see cref="CodexHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful
/// install.
/// </summary>
/// <remarks>
/// The settings file itself is written by
/// <c>SetupCommand.InstallPlugin</c>; this type owns only the marker
/// side-channel and pre-marker detection. The marketplace source path
/// is absolute and changes between npm installs, so a refresh on
/// upgrade is meaningful — not just for command-string drift.
/// </remarks>
public static class ClaudePluginInstaller {
    public const string MarkerFileName = ".kapacitor-plugin-version";

    /// <summary>
    /// True when the user has previously installed the kapacitor Claude
    /// plugin via setup or <c>kapacitor plugin install</c>. Detection is
    /// marker OR an existing kapacitor entry in <paramref name="settingsPath"/>
    /// (either <c>enabledPlugins["kapacitor@kapacitor"]</c> or
    /// <c>extraKnownMarketplaces["kapacitor"]</c>) so pre-marker installs
    /// are picked up on the first marker-aware upgrade.
    /// </summary>
    public static bool IsInstalled(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(settingsPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;

            if (root["enabledPlugins"] is JsonObject enabled &&
                enabled["kapacitor@kapacitor"] is JsonValue v &&
                v.TryGetValue<bool>(out var on) && on) {
                return true;
            }

            if (root["extraKnownMarketplaces"] is JsonObject marketplaces &&
                marketplaces["kapacitor"] is not null) {
                return true;
            }
        } catch {
            // Malformed JSON → treat as not installed.
        }
        return false;
    }

    public static string? ReadMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try {
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        } catch {
            return null;
        }
    }

    public static void WriteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), KapacitorVersion.Current());
        } catch {
            // Best effort. Worst case the next upgrade re-runs the install
            // unconditionally, which is idempotent.
        }
    }

    public static void DeleteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* non-fatal */ }
    }
}

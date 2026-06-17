using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Kiro;

/// <summary>
/// Marker + detection helpers for <c>~/.kiro/agents/kcap.json</c>. Mirror of
/// <see cref="Copilot.CopilotHooksInstaller"/>: the npm postinstall hook calls
/// <see cref="IsInstalled"/> to gate the upgrade-time refresh, and
/// <see cref="WriteMarker"/> stamps the version after a successful write.
/// </summary>
public static class KiroHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    /// <summary>
    /// True when the user has previously installed Kiro hooks via setup or
    /// <c>kcap plugin install --kiro</c>. Marker file presence OR an existing
    /// <c>kcap hook --kiro</c> entry in the agent JSON — the hooks-json fallback
    /// covers pre-marker installs.
    /// </summary>
    public static bool IsInstalled(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(agentJsonPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(agentJsonPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            foreach (var (_, entries) in hooks) {
                if (entries is not JsonArray arr) continue;

                if (arr.Any(KiroHooksParser.EntryReferencesCapacitorKiroHook)) {
                    return true;
                }
            }
        } catch { /* Malformed → treat as not installed. */ }
        return false;
    }

    public static string? ReadMarker(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    public static void WriteMarker(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    public static void DeleteMarker(string agentJsonPath) {
        var dir = Path.GetDirectoryName(agentJsonPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }
}

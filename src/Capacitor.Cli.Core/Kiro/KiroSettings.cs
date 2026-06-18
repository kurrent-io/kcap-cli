using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Kiro;

/// <summary>
/// Reads/writes the <c>chat.defaultAgent</c> key in Kiro's settings file
/// (<c>~/.kiro/settings/cli.json</c>) — the same key <c>kiro-cli agent
/// set-default</c> persists. Hooks only fire for the active agent and there is no
/// global/default-agent hook, so transparent capture requires making kcap's
/// (cloned) agent the launch default. Other settings keys (e.g.
/// <c>chat.defaultModel</c>) are preserved.
/// </summary>
public static class KiroSettings {
    const string DefaultAgentKey = "chat.defaultAgent";

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>Current default-agent name, or null when unset / file absent / malformed.</summary>
    public static string? ReadDefaultAgent(string settingsPath) {
        try {
            if (!File.Exists(settingsPath)) return null;
            return JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root
                && root[DefaultAgentKey]?.GetValue<string>() is { Length: > 0 } a
                    ? a
                    : null;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Sets <c>chat.defaultAgent</c> to <paramref name="agentName"/>, preserving
    /// every other key, creating the file/dir if needed. Returns false on I/O
    /// failure (caller surfaces it).
    /// </summary>
    public static bool SetDefaultAgent(string settingsPath, string agentName) {
        try {
            var root = (File.Exists(settingsPath) ? JsonNode.Parse(File.ReadAllText(settingsPath)) : null) as JsonObject
                    ?? new JsonObject();
            root[DefaultAgentKey] = agentName;

            var dir = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));
            return true;
        } catch {
            return false;
        }
    }
}

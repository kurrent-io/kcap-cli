using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// Vendor-neutral. Reads <c>response.version</c> from a Kurrent Capacitor
/// hook response and, when the server is strictly newer than the local CLI,
/// returns a short plain-text fragment that downstream vendor-specific
/// delivery shims (Claude Code today, Codex/Cursor later) wrap into their
/// native "additional context" channels.
///
/// Pure function: no I/O, no <c>Console</c>, no JSON envelope.
/// </summary>
public static class VersionNudgeEmitter {
    public static string? BuildFragment(JsonNode? responseNode, string currentCliVersion) {
        if (responseNode is not JsonObject obj) return null;

        string? serverVersion;
        try {
            serverVersion = obj["version"]?.GetValue<string>();
        } catch {
            return null;
        }

        if (!SemverCompare.IsNewer(serverVersion, currentCliVersion)) return null;

        return
            $"A newer kcap version is available: {currentCliVersion} → {serverVersion}.\n" +
            "Offer the user to upgrade by running: npm install -g @kurrent/kcap";
    }
}

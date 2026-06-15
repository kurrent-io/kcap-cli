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

        // Strip "+buildmetadata" before showing the strings to the user.
        // SemverCompare ignores the suffix for the comparison itself; this
        // makes the rendered fragment match what users typed to install the
        // CLI (e.g. "0.6.3") rather than the MinVer commit-SHA form.
        return
            $"A newer kcap version is available: {StripBuildMetadata(currentCliVersion)} → {StripBuildMetadata(serverVersion)}.\n" +
            "Offer the user to upgrade by running: kcap update";
    }

    static string StripBuildMetadata(string? v) {
        if (v is null) return "";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}

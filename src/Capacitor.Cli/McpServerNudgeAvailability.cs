using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli;

/// <summary>
/// Is a kcap MCP server actually MATERIALIZED in the invoking harness's on-disk config? A
/// SessionStart nudge tells the agent to use tools that exist only if that server is registered, so
/// a stale install (an upgraded CLI whose harness config predates the server) must not be nudged
/// toward a tool it lacks.
///
/// <para>This reads the real config entry — not an ownership marker — so it also catches a
/// manually-removed, disabled, or malformed entry. It is a CONFIG-LEVEL check, deliberately NOT a
/// runtime health probe (we never spawn/handshake the server at hook time). The one residual — a
/// materialized entry whose server nonetheless fails to launch — yields at worst a benign
/// tool-not-found, repaired by <c>kcap setup</c>/<c>kcap doctor</c>.</para>
///
/// <para>Fails CLOSED: any absent / disabled / malformed / unreadable config suppresses the nudge.</para>
/// </summary>
static class McpServerNudgeAvailability {
    /// <param name="serverName">The registration name, e.g. <c>kcap-plans</c>.</param>
    /// <param name="codexConfigPath">Overrides the Codex <c>config.toml</c> path (test seam); null uses the default.</param>
    public static bool IsRegisteredFor(HarnessId harness, HarnessRegistry harnesses, string serverName, string? codexConfigPath = null) {
        try {
            return harness switch {
                // Claude loads the plugin's bundled .mcp.json, so the server is available exactly when
                // the plugin is effectively installed AND the copy Claude loads names it.
                HarnessId.Claude  => ClaudePluginInstaller.EffectiveMcpJsonPath(harnesses.Of<ClaudeHarness>().Paths.UserSettings) is { } mcpJson
                                  && JsonBlockHasServer(mcpJson, "mcpServers", serverName),
                HarnessId.Codex   => CodexHas(serverName, codexConfigPath ?? harnesses.Of<CodexHarness>().Paths.ConfigToml),
                HarnessId.Cursor  => JsonBlockHasServer(harnesses.Of<CursorHarness>().Paths.UserMcpJson, "mcpServers", serverName),
                HarnessId.Copilot => JsonBlockHasServer(harnesses.Of<CopilotHarness>().Paths.McpConfigJson, "mcpServers", serverName),
                HarnessId.Gemini  => JsonBlockHasServer(harnesses.Of<GeminiHarness>().Paths.SettingsJson, "mcpServers", serverName),
                HarnessId.Kiro    => JsonBlockHasServer(harnesses.Of<KiroHarness>().Paths.SettingsMcpJson, "mcpServers", serverName),
                // OpenCode's block key is `mcp`, not `mcpServers`.
                HarnessId.OpenCode    => JsonBlockHasServer(harnesses.Of<OpenCodeHarness>().Paths.McpConfigJson, "mcp", serverName),
                HarnessId.Antigravity => JsonBlockHasServer(harnesses.Of<AntigravityHarness>().Paths.McpConfigJson, "mcpServers", serverName),
                HarnessId.Pi          => PiHas(harnesses, PiToken(serverName)),
                _ => false
            };
        } catch {
            return false; // fail closed
        }
    }

    /// <summary>The Pi bridge lists servers by their <c>kcap mcp &lt;name&gt;</c> subcommand.</summary>
    static string PiToken(string serverName) =>
        serverName.StartsWith("kcap-", StringComparison.Ordinal) ? serverName["kcap-".Length..] : serverName;

    static bool CodexHas(string serverName, string codexConfigPath) {
        try {
            // ReadMcpServerCommands requires each returned table to carry a `command` string, so a
            // malformed/command-less [mcp_servers.<name>] table does NOT count (fail-closed).
            // Codex has no per-server enable flag, so a valid command table is a live registration.
            return CodexConfigToml.ReadMcpServerCommands(codexConfigPath)
                .Any(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        } catch {
            return false;
        }
    }

    static bool PiHas(HarnessRegistry harnesses, string token) {
        try {
            var path = harnesses.Of<PiHarness>().Paths.KcapMcpExtension;
            if (!File.Exists(path)) return false;
            // Strip JS comments FIRST so a commented-out `KCAP_MCP_SERVERS = [...]` before the real
            // declaration can't be matched, then find the real `KCAP_MCP_SERVERS = [ … ]` array and
            // require the token as an exact array ELEMENT — not a substring, so a token inside an
            // unrelated string doesn't count either.
            var content = StripJsComments(File.ReadAllText(path));
            for (var k = content.IndexOf("KCAP_MCP_SERVERS", StringComparison.Ordinal);
                 k >= 0;
                 k = content.IndexOf("KCAP_MCP_SERVERS", k + 1, StringComparison.Ordinal)) {
                var eq = content.IndexOf('=', k);
                if (eq < 0) continue;
                var open = content.IndexOf('[', eq);
                if (open < 0) continue;
                var close = content.IndexOf(']', open);
                if (close < 0) return false;
                var elements = content.Substring(open + 1, close - open - 1)
                    .Split(',')
                    .Select(e => e.Trim());
                return elements.Any(e => e == $"\"{token}\"" || e == $"'{token}'");
            }
            return false;
        } catch {
            return false;
        }
    }

    /// <summary>Removes JS line (<c>//…</c>) and block (<c>/* … */</c>) comments so a commented-out
    /// server list can't be mistaken for the real declaration. Not string-literal-aware, which is
    /// safe here: the generated extension carries no <c>//</c> or <c>/*</c> inside the single-line
    /// string literals near the declaration.</summary>
    static string StripJsComments(string s) {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++) {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/') {
                i += 2;
                while (i < s.Length && s[i] != '\n') i++;
                if (i < s.Length) sb.Append('\n'); // keep the newline
            } else if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*') {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i++; // land on the '/' of '*/'; the loop's i++ steps past it
            } else {
                sb.Append(s[i]);
            }
        }
        return sb.ToString();
    }

    static bool JsonBlockHasServer(string path, string blockKey, string serverName) {
        try {
            if (!File.Exists(path)) return false;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return false;
            if (root[blockKey] is not JsonObject block) return false;

            foreach (var (name, entry) in block) {
                if (!string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase)) continue;
                // A materialized MCP entry is always an object (command/args or type/enabled). Anything
                // else — null, a string, an array — is malformed and fails closed.
                if (entry is not JsonObject o) return false;
                // Honor the harness's own switch STRICTLY: OpenCode's `enabled` counts only as a
                // Boolean true, Kiro's `disabled` only as a Boolean false; a non-Boolean or any other
                // shape suppresses. An absent switch means enabled.
                var enabled = !o.TryGetPropertyValue("enabled", out var enNode)
                           || (enNode is JsonValue enVal && enVal.TryGetValue<bool>(out var on) && on);
                var live    = !o.TryGetPropertyValue("disabled", out var disNode)
                           || (disNode is JsonValue disVal && disVal.TryGetValue<bool>(out var off) && !off);

                return enabled && live;
            }
            return false;
        } catch {
            return false;
        }
    }
}

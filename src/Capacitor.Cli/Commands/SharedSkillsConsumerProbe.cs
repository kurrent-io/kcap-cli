using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The integrations reading the shared <c>~/.agents/skills</c> tree that are still configured at their
/// default locations: what stands in for <see cref="SharedSkillsConsumers"/> on a tree installed before
/// it was recorded. Hooks or kcap's MCP entries are the evidence; a configured harness whose skills
/// were turned off reads as a consumer too, which keeps the skills rather than deleting another's.
/// </summary>
public static class SharedSkillsConsumerProbe {
    /// <summary>The harnesses whose installs write the shared tree. Kiro and Antigravity read their own,
    /// and Claude gets its skills from the plugin.</summary>
    public static IReadOnlyList<HarnessId> Readers { get; } = [
        HarnessId.Codex, HarnessId.Cursor, HarnessId.Copilot, HarnessId.Gemini, HarnessId.Pi,
        HarnessId.OpenCode, HarnessId.MistralVibe, HarnessId.Grok, HarnessId.Muse,
    ];

    public static IEnumerable<string> Configured(HarnessRegistry harnesses, UserHome home) =>
        Readers.Where(id => IsConfigured(id, harnesses, home)).Select(id => id.VendorId);

    static bool IsConfigured(HarnessId id, HarnessRegistry harnesses, UserHome home) {
        switch (id) {
            case HarnessId.Codex: {
                var p = harnesses.Of<CodexHarness>().Paths;
                return CodexHooksInstaller.IsInstalled(p.UserHooksJson) || CodexConfigToml.HasMcpInstallRecord(p.ConfigToml);
            }
            case HarnessId.Cursor: {
                var p = harnesses.Of<CursorHarness>().Paths;
                return CursorHooksInstaller.IsInstalled(p.UserHooksJson) || HarnessMcpProjections.Cursor.OwnsAnything(p.UserMcpJson, home);
            }
            case HarnessId.Copilot: {
                var p = harnesses.Of<CopilotHarness>().Paths;
                return CopilotHooksInstaller.IsInstalled(p.KcapHooksJson) || HarnessMcpProjections.Copilot.OwnsAnything(p.McpConfigJson, home);
            }
            case HarnessId.Gemini: {
                var p = harnesses.Of<GeminiHarness>().Paths;
                return GeminiHooksInstaller.IsInstalled(p.SettingsJson) || HarnessMcpProjections.Gemini.OwnsAnything(p.SettingsJson, home);
            }
            case HarnessId.Pi: {
                var p = harnesses.Of<PiHarness>().Paths;
                return PiExtensionInstaller.IsInstalled(p.KcapExtension) || PiMcpExtensionInstaller.IsInstalled(p.KcapMcpExtension);
            }
            case HarnessId.OpenCode: {
                var p = harnesses.Of<OpenCodeHarness>().Paths;
                return OpenCodeExtensionInstaller.IsInstalled(p.KcapPlugin) || HarnessMcpProjections.OpenCode.OwnsAnything(p.McpConfigJson, home);
            }
            case HarnessId.MistralVibe: {
                var p = harnesses.Of<MistralVibeHarness>().Paths;
                return MistralVibeHooksInstaller.IsInstalled(p.HooksToml) || MistralVibeConfigToml.OwnsAnything(p.ConfigToml);
            }
            case HarnessId.Grok:
                return GrokHooksInstaller.IsInstalled(harnesses.Of<GrokHarness>().Paths.KcapHooksJson);
            case HarnessId.Muse: {
                var p = harnesses.Of<MuseHarness>().Paths;
                return MuseHooksInstaller.IsInstalled(p.SettingsJson) || MuseSettingsJson.OwnsAnything(p.SettingsJson, home);
            }
            default:
                return false;
        }
    }
}

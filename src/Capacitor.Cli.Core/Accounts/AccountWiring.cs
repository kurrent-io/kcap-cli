using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Accounts;

public static class AccountWiring {
    const string NetworkStep = "network";

    public static bool Succeeded(IReadOnlyList<WiringStep> steps) => steps.All(s => s.Succeeded);

    /// <summary>A failed sandbox-network step is a warning: the account still records.</summary>
    public static bool HasFatalFailure(IReadOnlyList<WiringStep> steps) =>
        steps.Any(s => !s.Succeeded && s.Name != NetworkStep);

    public static string FailureSummary(IReadOnlyList<WiringStep> steps) =>
        string.Join(", ", steps.Where(s => !s.Succeeded).Select(s => $"{s.Name}: {s.Detail}"));

    public static IReadOnlyList<WiringStep> Wire(VendorAccount account, UserHome home, WiringOptions options) =>
        account.Vendor switch {
            HarnessId.Claude => WireClaude(AccountLayouts.Claude(home, account.Directory), options),
            HarnessId.Codex  => WireCodex(AccountLayouts.Codex(home, account.Directory), options),
            _                => [new WiringStep("vendor", false, $"{account.Vendor} has no accounts")],
        };

    public static IReadOnlyList<WiringStep> Unwire(VendorAccount account, UserHome home) {
        if (!Directory.Exists(account.Directory))
            return [new WiringStep("directory", true, $"{account.Directory} no longer exists; nothing to unwire")];

        return account.Vendor switch {
            HarnessId.Claude => [Step("plugin", ClaudePluginWriter.Remove(AccountLayouts.Claude(home, account.Directory).UserSettings))],
            HarnessId.Codex  => UnwireCodex(AccountLayouts.Codex(home, account.Directory)),
            _                => [],
        };
    }

    public static RecordingState State(VendorAccount account, UserHome home) {
        switch (account.Vendor) {
            case HarnessId.Claude: {
                var settings = AccountLayouts.Claude(home, account.Directory).UserSettings;
                if (IsMalformed(settings)) return RecordingState.Broken;
                if (!ClaudePluginInstaller.IsPluginEnabled(settings)) return RecordingState.NotWired;
                if (ClaudePluginInstaller.IsEffectivelyInstalled(settings)) return RecordingState.Recording;

                // Claude installs the plugin on its next launch, so a wired account has no record until then.
                return ClaudePluginInstaller.HasInstallRecord(settings) ? RecordingState.Broken : RecordingState.Installed;
            }
            case HarnessId.Codex: {
                var hooks = AccountLayouts.Codex(home, account.Directory).UserHooksJson;
                if (IsMalformed(hooks)) return RecordingState.Broken;
                return CodexHooksInstaller.ReferencesKcapHook(hooks) ? RecordingState.Installed : RecordingState.NotWired;
            }
            default:
                return RecordingState.NotWired;
        }
    }

    static IReadOnlyList<WiringStep> WireClaude(ClaudePaths paths, WiringOptions options) =>
        options.PluginDir is null
            ? [new WiringStep("plugin", false, "kcap plugin directory not found; reinstall kcap via npm")]
            : [Step("plugin", ClaudePluginWriter.Install(paths.UserSettings, options.PluginDir))];

    static IReadOnlyList<WiringStep> WireCodex(CodexPaths paths, WiringOptions options) {
        var steps = new List<WiringStep> { Step("hooks", CodexHooksWriter.Install(paths.UserHooksJson)) };

        if (options.PluginDir is { } plugin) {
            var ok = AgentsSkillsInstaller.Install(Path.Combine(plugin, "skills"), options.AgentsSkillsDir);
            steps.Add(new WiringStep("skills", ok, ok ? options.AgentsSkillsDir : "could not install agent skills"));
        }

        if (options.NetworkAllowDomains is { } domains)
            steps.Add(Step(NetworkStep, CodexConfigToml.EnableNetworkAccess(domains, paths.ConfigToml)));

        steps.Add(Step("mcp", CodexConfigToml.RegisterKcapMcpServers(paths.ConfigToml, options.ResolveMcpBinaryPath)));

        return steps;
    }

    static IReadOnlyList<WiringStep> UnwireCodex(CodexPaths paths) => [
        Step("hooks", CodexHooksWriter.Remove(paths.UserHooksJson)),
        Step("mcp", CodexConfigToml.UnregisterKcapMcpServers(paths.ConfigToml)),
    ];

    static WiringStep Step(string name, SettingsEdit edit) => edit switch {
        SettingsEdit.Malformed => new(name, false, "settings file unreadable; left unchanged"),
        SettingsEdit.Failed    => new(name, false, "could not write the settings file"),
        _                      => new(name, true, edit.ToString()),
    };

    static WiringStep Step(string name, CodexConfigToml.Change change) =>
        new(name, change is not CodexConfigToml.Change.Failed, change.ToString());

    static bool IsMalformed(string path) {
        if (!File.Exists(path)) return false;
        try {
            var text = File.ReadAllTextShared(path);
            return !string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is not JsonObject;
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return true;
        }
    }
}

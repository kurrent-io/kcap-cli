using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Accounts;

public static class AccountWiring {
    const string NetworkStep          = "network";
    const string StatusLineStep       = "statusline";
    const string StatusLineBackupStep = "statusline backup";

    public static bool Succeeded(IReadOnlyList<WiringStep> steps) => steps.All(s => s.Succeeded);

    /// <summary>A failed sandbox-network step is a warning: the account still records. So is a status line
    /// removed without a backup to restore, since there is nothing left to retry.</summary>
    public static bool HasFatalFailure(IReadOnlyList<WiringStep> steps) =>
        steps.Any(s => !s.Succeeded && s.Name is not (NetworkStep or StatusLineBackupStep));

    public static string FailureSummary(IReadOnlyList<WiringStep> steps) =>
        string.Join(", ", steps.Where(s => !s.Succeeded).Select(s => $"{s.Name}: {s.Detail}"));

    /// <summary>The caller holds <paramref name="store"/>'s lock.</summary>
    public static IReadOnlyList<WiringStep> Wire(VendorAccount account, UserHome home, WiringOptions options, AccountStore store) =>
        account.Vendor switch {
            HarnessId.Claude => [WireClaude(AccountLayouts.Claude(home, account.Directory), options), WireStatusLine(account.Id, home, store)],
            HarnessId.Codex  => WireCodex(AccountLayouts.Codex(home, account.Directory), options),
            _                => [new WiringStep("vendor", false, $"{account.Vendor} has no accounts")],
        };

    /// <summary>The caller holds <paramref name="store"/>'s lock.</summary>
    public static IReadOnlyList<WiringStep> Unwire(VendorAccount account, UserHome home, AccountStore store) {
        if (!Directory.Exists(account.Directory))
            return [new WiringStep("directory", true, $"{account.Directory} no longer exists; nothing to unwire")];

        return account.Vendor switch {
            HarnessId.Claude => [
                Step("plugin", ClaudePluginWriter.Remove(AccountLayouts.Claude(home, account.Directory).UserSettings)),
                UnwireStatusLine(account.Id, home, store),
            ],
            HarnessId.Codex => UnwireCodex(AccountLayouts.Codex(home, account.Directory)),
            _               => [],
        };
    }

    /// <summary>The Claude status line alone, for the environment's default directory, whose plugin is wired by
    /// its own layout path. The caller holds <paramref name="store"/>'s lock; the entry is re-read under it, because
    /// an account resolved before the lock can carry a backup another process has since replaced.</summary>
    public static WiringStep WireStatusLine(string accountId, UserHome home, AccountStore store) =>
        StatusLine(store, accountId, account => {
            var removed = false;
            var edit = ClaudeStatusLine.Install(
                AccountLayouts.Claude(home, account.Directory).UserSettings, accountId, account.StatusLine,
                backup => {
                    var kept = SetBackup(store, accountId, backup);
                    removed = !kept;
                    return kept;
                },
                ClaudeStatusLineCommand.PosixShell(Environment.GetEnvironmentVariable));

            return removed ? AccountRemoved : Step(StatusLineStep, edit);
        });

    /// <summary>Discards the backup once the settings no longer need it, and keeps it when the edit failed.</summary>
    public static WiringStep UnwireStatusLine(string accountId, UserHome home, AccountStore store) =>
        StatusLine(store, accountId, account => {
            var (edit, missing) = ClaudeStatusLine.Remove(AccountLayouts.Claude(home, account.Directory).UserSettings, account.StatusLine);
            var step            = Step(StatusLineStep, edit);
            if (!step.Succeeded) return step;

            if (account.StatusLine is not null) SetBackup(store, accountId, null);

            return missing ? new(StatusLineBackupStep, false, "no backup of the original status line; removed kcap's command") : step;
        });

    static readonly WiringStep AccountRemoved = new(StatusLineStep, false, "account removed");

    static WiringStep StatusLine(AccountStore store, string accountId, Func<VendorAccount, WiringStep> change) {
        try {
            return store.FindHeld(accountId) is { } account ? change(account) : AccountRemoved;
        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) {
            return new(StatusLineStep, false, $"account registry unusable: {ex.Message}");
        }
    }

    static bool SetBackup(AccountStore store, string accountId, StatusLineBackup? backup) =>
        store.MutateHeld(r => r.Accounts.FirstOrDefault(a => a.Id == accountId) switch {
            null                                     => (r, false),
            { } entry when entry.StatusLine == backup => (r, true),
            _ => (r with { Accounts = [.. r.Accounts.Select(a => a.Id == accountId ? a with { StatusLine = backup } : a)] }, true),
        }).Result;

    /// <summary>A Codex account reads Recording only when its stored trust answer was given for the files on disk now.</summary>
    public static RecordingState State(VendorAccount account, UserHome home) {
        var state = State(account.Vendor, account.Directory, home);
        if (state != RecordingState.Installed || account.Vendor != HarnessId.Codex) return state;

        if (account.HookTrust != HookTrustStates.Trusted || account.HookTrustFingerprint is not { } stored) return state;

        return CodexTrustFingerprint.Of(AccountLayouts.Codex(home, account.Directory)) == stored ? RecordingState.Recording : state;
    }

    public static RecordingState State(HarnessId vendor, string directory, UserHome home) {
        switch (vendor) {
            case HarnessId.Claude: {
                var settings = AccountLayouts.Claude(home, directory).UserSettings;
                if (IsMalformed(settings)) return RecordingState.Broken;
                if (!ClaudePluginInstaller.IsPluginEnabled(settings)) return RecordingState.NotWired;
                if (ClaudePluginInstaller.IsEffectivelyInstalled(settings)) return RecordingState.Recording;

                // Claude installs the plugin on its next launch, so a wired account has no record until then.
                return ClaudePluginInstaller.HasInstallRecord(settings) ? RecordingState.Broken : RecordingState.Installed;
            }
            case HarnessId.Codex: {
                var hooks = AccountLayouts.Codex(home, directory).UserHooksJson;
                // Codex refuses a file with a byte-order mark; rewiring rewrites it without one.
                if (IsMalformed(hooks) || CodexHooksInstaller.StartsWithByteOrderMark(hooks)) return RecordingState.Broken;
                return CodexHooksInstaller.ReferencesKcapHook(hooks) ? RecordingState.Installed : RecordingState.NotWired;
            }
            default:
                return RecordingState.NotWired;
        }
    }

    static WiringStep WireClaude(ClaudePaths paths, WiringOptions options) =>
        options.PluginDir is null
            ? new WiringStep("plugin", false, "kcap plugin directory not found; reinstall kcap via npm")
            : Step("plugin", ClaudePluginWriter.Install(paths.UserSettings, options.PluginDir));

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

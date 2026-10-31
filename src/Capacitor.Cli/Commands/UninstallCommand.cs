using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Completely removes kcap from the local machine: removes the local server
/// (purging its data unless <c>--keep-config</c>), stops daemons, kills
/// watcher processes, strips kcap entries from user-level Claude / Codex /
/// Cursor / Gemini hook files, deletes the kcap-owned Copilot hooks file, the
/// kcap Kiro agent (~/.kiro/agents/kcap.json) — restoring the default agent it
/// replaced — and the Pi live-ingest extension (~/.pi/agent/extensions/kcap.ts),
/// removes agent skills (and legacy Codex skills), deletes the daemon state
/// directory, the kcap config directory and, last, the vendor account registry.
///
/// With <c>--project</c>, also strips kcap entries from the cwd's git-root
/// <c>.claude/settings.local.json</c> and <c>.codex/hooks.json</c>. Project-scope
/// Cursor hooks are not a thing — Cursor only reads its user-scope hooks.json.
/// Per-agent selective cleanup is intentionally out of scope here; this command
/// covers all known agents.
/// </summary>
sealed class UninstallCommand(
        DaemonStore store, ConfigRoot config, UserHome home, HarnessRegistry harnesses, BinaryProbe binaries,
        AgentsPaths agents, WorkingDirectory workdir, IOutgoingRecorders recorders, ILocalStackFactory stacks,
        KcapLinkRemover links, StandardStreams console) {
    static readonly string[] StackDirectories = ["bin", "run", "data", "logs"];

    public async Task<int> HandleAsync(string[] args) {
        var skipPrompt     = args.Contains("--yes") || args.Contains("-y");
        var keepConfig     = args.Contains("--keep-config");
        var includeProject = args.Contains("--project");
        var force          = args.Contains("--force");
        var purgeLocal     = args.Contains("--purge-local");
        var keepPathLinks  = args.Contains("--keep-path-links");

        string? projectRoot = null;

        if (includeProject) {
            projectRoot = GitRepository.FindRoot(workdir.Path);

            if (projectRoot is null) {
                await console.Error.WriteLineAsync(
                    $"--project requires a git working tree, but '{workdir.Path}' is not inside one.");
                await console.Error.WriteLineAsync(
                    "Re-run from inside your repo, or drop --project to only remove user-level configuration.");

                return 1;
            }
        }

        LocalStack?     local       = null;
        LocalStackState localState  = LocalStackState.NotInstalled.Instance;
        var             localFailed = false;

        try {
            local      = stacks.Create();
            localState = local.Read();
        } catch (LocalInstallFailure ex) when (ex.Code == LocalInstallFailureCode.UnsupportedPlatform) {
            // No local edition runs on this platform, so there is none to remove.
        } catch (LocalInstallFailure ex) {
            await console.Error.WriteLineAsync($"Could not read the local server: {ex.Message}");
            localFailed = true;
        }

        if (localState is LocalStackState.Unreadable(var unreadablePath, var unreadableReason)) {
            await console.Error.WriteLineAsync(
                $"{unreadablePath} cannot be read ({unreadableReason}), so the local server is left as it is and the config directory is kept.");
            localFailed = true;
        }

        // An interrupted setup may finish as an installation, so it needs the same consent as one.
        var removeLocal = localState is LocalStackState.Installed or LocalStackState.Interrupted;

        if (LocalConsentError(localState, local?.Root.Directory ?? "", skipPrompt, purgeLocal, keepConfig) is { } refusal) {
            await console.Error.WriteLineAsync(refusal);
            return 1;
        }

        if (!force) {
            var busy = await recorders.BusyAsync();

            if (busy.Count > 0) {
                foreach (var line in busy) await console.Error.WriteLineAsync(line);

                await console.Error.WriteLineAsync("Stop them, or pass --force to end them. Nothing was changed.");

                return 1;
            }
        }

        var configDir = config.Directory;

        await console.Out.WriteLineAsync("This will remove kcap from your machine:");
        await console.Out.WriteLineAsync("  • Stop any running daemons and watcher processes");

        switch (localState) {
            case LocalStackState.Installed { Installation.ServerUrl: var url }:
                await console.Out.WriteLineAsync(keepConfig
                    ? $"  • Remove the local server at {url}, keeping its data"
                    : $"  • Delete the local server at {url} and every session it recorded");
                break;
            case LocalStackState.Interrupted(var operation, _):
                // An interrupted purge is always finished, whatever --keep-config says.
                await console.Out.WriteLineAsync(keepConfig && operation != LocalPurgeJournal.Operation
                    ? $"  • Finish an interrupted {operation} of the local server in {local!.Root.Directory}, then remove it, keeping its data"
                    : $"  • Finish an interrupted {operation} of the local server in {local!.Root.Directory}, then delete it and every session it recorded");
                break;
        }

        await console.Out.WriteLineAsync($"  • Remove kcap entries from {harnesses.Of<ClaudeHarness>().Paths.UserSettings}");
        await console.Out.WriteLineAsync($"  • Remove kcap entries from {harnesses.Of<CodexHarness>().Paths.UserHooksJson}");
        await console.Out.WriteLineAsync($"  • Remove kcap entries from {harnesses.Of<CursorHarness>().Paths.UserHooksJson}");
        await console.Out.WriteLineAsync($"  • Remove {harnesses.Of<CopilotHarness>().Paths.KcapHooksJson}");
        await console.Out.WriteLineAsync($"  • Remove kcap entries from {harnesses.Of<GeminiHarness>().Paths.SettingsJson}");
        await console.Out.WriteLineAsync($"  • Remove {harnesses.Of<KiroHarness>().Paths.KcapAgentJson} and restore the previous default Kiro agent");
        await console.Out.WriteLineAsync($"  • Remove {harnesses.Of<PiHarness>().Paths.KcapExtension}");
        await console.Out.WriteLineAsync($"  • Remove {harnesses.Of<OpenCodeHarness>().Paths.KcapPlugin}");
        await console.Out.WriteLineAsync($"  • Remove the kcap plugin from {harnesses.Of<AntigravityHarness>().Paths.PluginDir}");
        await console.Out.WriteLineAsync($"  • Remove kcap entries from {harnesses.Of<MuseHarness>().Paths.SettingsJson}");
        await console.Out.WriteLineAsync($"  • Remove kcap entries from {harnesses.Of<MistralVibeHarness>().Paths.HooksToml} and {harnesses.Of<MistralVibeHarness>().Paths.ConfigToml}");
        await console.Out.WriteLineAsync($"  • Remove agent skills under {agents.UserSkillsDir} and vendor-specific skill trees");
        await console.Out.WriteLineAsync($"  • Remove {Path.Combine(home.Path, ".cache", "kcap")} and legacy Cursor pending recordings");

        var pathLinks = !keepPathLinks && !OperatingSystem.IsWindows() ? links.Find() : [];

        foreach (var link in pathLinks)
            await console.Out.WriteLineAsync($"  • Remove the kcap command link at {link}");

        IReadOnlyList<string> profiles = keepPathLinks || OperatingSystem.IsWindows() ? [] : ShellProfile.Removable(home, pathLinks);

        foreach (var profile in profiles)
            await console.Out.WriteLineAsync($"  • Remove the PATH line kcap added to {profile}");

        if (projectRoot is not null) {
            await console.Out.WriteLineAsync(
                $"  • Remove kcap entries from {Path.Combine(projectRoot, ".claude", "settings.local.json")}");
            await console.Out.WriteLineAsync(
                $"  • Remove kcap entries from {Path.Combine(projectRoot, ".codex", "hooks.json")}");
        }

        if (!keepConfig) {
            await console.Out.WriteLineAsync(local is not null && Directory.Exists(local.Root.Directory)
                ? $"  • Remove kcap configuration from {configDir} (local lifecycle safety metadata is kept)"
                : $"  • Delete the kcap config directory ({configDir})");
        }

        // KCAP_CONFIG_DIR never moves it, so the config-directory delete does not cover it.
        // A link there could lead anywhere, so only the real directory counts as kcap's. A linked
        // ~/.config is allowed: dotfile managers link it, and kcap/ inside it is still kcap's.
        var ownsDaemonDir = PathsEqual(store.Directory, DaemonStore.DefaultDirectory(home))
                         && new DirectoryInfo(store.Directory).LinkTarget is null
                         && new DirectoryInfo(Path.GetDirectoryName(store.Directory)!).LinkTarget is null;
        if (ownsDaemonDir)
            await console.Out.WriteLineAsync($"  • Delete the daemon state directory ({store.Directory})");

        await console.Out.WriteLineAsync(KeptProgramLine(
            InstallProvenance.Kind(), ScriptInstallLayout.OfRunningBinary()?.Root, SnapLayout.OfRunningBinary()?.Name,
            Environment.ProcessPath));

        if (!skipPrompt) {
            await console.Out.WriteAsync("Proceed? [y/N] ");
            var reply = await console.In.ReadLineAsync();

            if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await console.Out.WriteLineAsync(CancelledLine);

                return 0;
            }
        }

        // An unreadable registry leaves the local server alone and keeps the config directory, but
        // integrations still go: nothing about them depends on the local server.
        if (localFailed) return await CleanupAsync(local?.Root.Directory, hadFailures: true);
        // Taking the lease creates the root, so a machine where no setup has started is cleaned without it.
        if (local is null || !Directory.Exists(local.Root.Directory)) return await CleanupAsync(null);

        var cleanupStarted = false;
        try {
            return await local.RunAsync(new SyncProgress<LocalInstallEvent>(e => LocalCommand.RenderRecovery(console.Out, e)), async (lease, state, ct) => {
                if (CanonicalPath.IsWithin(config.Directory, local.Root.Directory))
                    throw new LocalLifecycleException("KCAP_CONFIG_DIR must be outside KCAP_LOCAL_ROOT to preserve lifecycle safety metadata.");
                if (state is LocalStackState.NotInstalled) {
                    var owner = local.Service.Owner(LocalServerUnit.ForInstallation(local.Root, 0));
                    if (owner.Loaded is not OwnerReading.Absent || owner.OnDisk is not OwnerReading.Absent
                        || StackDirectories.Any(name => Directory.Exists(Path.Combine(local.Root.Directory, name))))
                        throw new LocalLifecycleException($"Local files or a service exist without an active registry in {local.Root.Directory}. Restore the registry or use the original KCAP_LOCAL_ROOT before uninstalling.");
                }
                var removalFailed = false;
                try {
                    var outcome = await new LocalUninstall(local.Service, home, recorders)
                        .RunAsync(lease, state, purge: !keepConfig, force, config.Directory, ct);
                    switch (outcome) {
                        case LocalUninstallOutcome.Purged:
                            await console.Out.WriteLineAsync($"Purged the local server in {local.Root.Directory}.");
                            break;
                        case LocalUninstallOutcome.Uninstalled:
                            await console.Out.WriteLineAsync($"Uninstalled the local server. Its data in {local.Root.Directory} is kept.");
                            break;
                    }
                } catch (Exception ex) when (ex is LocalLifecycleException or LocalInstallFailure or LocalUpgradeFailure or IOException or UnauthorizedAccessException) {
                    await console.Error.WriteLineAsync($"Could not remove the local server: {ex.Message}");
                    removalFailed = true;
                }
                cleanupStarted = true;
                return await CleanupAsync(local.Root.Directory, removalFailed);
            }, CancellationToken.None, force, beforeRecovery: state => {
                if (LocalConsentError(state, local.Root.Directory, skipPrompt, purgeLocal, keepConfig) is { } error)
                    throw new LocalLifecycleException(error);
                if (purgeLocal) return;

                var purgeAppeared = state is LocalStackState.Interrupted(LocalPurgeJournal.Operation, _)
                    && localState is not LocalStackState.Interrupted(LocalPurgeJournal.Operation, _);
                if (purgeAppeared)
                    throw new LocalLifecycleException("A local purge appeared after confirmation. Run uninstall again to review its data deletion.");

                var appeared = !removeLocal && state is LocalStackState.Installed or LocalStackState.Interrupted;
                var replaced = state is LocalStackState.Installed current && localState is LocalStackState.Installed previous
                    && current.Installation.Id != previous.Installation.Id;
                if (appeared || replaced)
                    throw new LocalLifecycleException("The local installation changed after confirmation. Run uninstall again to review it.");
            });
        } catch (Exception ex) when (ex is LocalLifecycleException or LocalInstallFailure or LocalUpgradeFailure or IOException or UnauthorizedAccessException) {
            // Past the local removal the config directory is not yet deleted, so a re-run can finish.
            await console.Error.WriteLineAsync(cleanupStarted
                ? $"kcap uninstall stopped: {ex.Message}. Re-run `kcap uninstall` to finish."
                : $"Could not remove the local server: {ex.Message}");
            return 1;
        }

        async Task<int> CleanupAsync(string? localRoot, bool hadFailures = false) {
            // Any failure keeps the config directory, so a re-run can finish.

            // Daemons hold lock files inside the config dir we're about to delete; one still running leaves the
            // config dir in place.
            if (!await recorders.StopAsync())
                hadFailures = true;

            var accountStore  = AccountStore.Beside(store);
            var env           = PluginEnvironment.FromProcess(await AppConfig.LoadProfileConfig(config), home, harnesses, binaries, accountStore)
                with { Stdout = console.Out, Stderr = console.Error };
            var pluginCommand = new PluginCommand(env, workdir);

            // User-level agent integrations. Each remove command is idempotent and
            // no-ops if the target file doesn't exist, so it's safe to call all of
            // them unconditionally without sniffing which agents are installed.
            if (await pluginCommand.HandleAsync(["plugin", "remove"])                  != 0) hadFailures = true; // Claude
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--codex"])       != 0) hadFailures = true; // Codex hooks + skills + legacy
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--cursor"])      != 0) hadFailures = true;
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--copilot"])     != 0) hadFailures = true;
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--gemini"])      != 0) hadFailures = true; // shared ~/.gemini/settings.json
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--kiro"])        != 0) hadFailures = true; // ~/.kiro/agents/kcap.json + restore previous default agent
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--pi"])          != 0) hadFailures = true; // Pi extension (~/.pi/agent/extensions/kcap.ts)
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--opencode"])    != 0) hadFailures = true; // OpenCode plugin (~/.config/opencode/plugins/kcap.ts)
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--antigravity"]) != 0) hadFailures = true; // Antigravity kcap plugin (~/.gemini/config/plugins/kcap/)
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--mistral-vibe"]) != 0) hadFailures = true;
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--grok"])        != 0) hadFailures = true; // ~/.grok/hooks/kcap.json
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--muse"])        != 0) hadFailures = true; // Muse hooks + MCP in settings.json

            // Skills are removed by --codex above, but call --skills explicitly in
            // case the user only ever installed Cursor / agent-agnostic skills and
            // never had Codex hooks (the --codex path short-circuits on a missing
            // hooks file).
            if (await pluginCommand.HandleAsync(["plugin", "remove", "--skills"]) != 0) hadFailures = true;

            if (pluginCommand.AccountsMayStillBeWired) {
                await console.Error.WriteLineAsync(
                    "Some vendor accounts may still record to kcap: check them with `kcap accounts`, then re-run `kcap uninstall`.");
                hadFailures = true;
            }

            if (!new GitHookInstaller(home).Remove()) {
                await console.Error.WriteLineAsync("Could not remove the kcap git hook. Fix the global git configuration and retry.");
                hadFailures = true;
            }

            // These installers drop their marker only when they changed the JSON, so a marker beside
            // hand-pruned entries would still read as installed. The Cursor MCP marker is not swept:
            // `plugin remove --cursor` keeps it on a failed unregister so a retry can find kcap's entries.
            ClaudePluginInstaller.DeleteMarker(harnesses.Of<ClaudeHarness>().Paths.UserSettings);
            CodexHooksInstaller.DeleteMarker(harnesses.Of<CodexHarness>().Paths.UserHooksJson);
            CursorHooksInstaller.DeleteMarker(harnesses.Of<CursorHarness>().Paths.UserHooksJson);
            GeminiHooksInstaller.DeleteMarker(harnesses.Of<GeminiHarness>().Paths.SettingsJson);
            // Kiro's remover owns the marker: it records the default agent needed by a retry.

            // Skill installer Remove uses the current SourceNames list, so any
            // kcap-* folder from an older release (renamed/retired skill)
            // would survive. Sweep the directory for our prefix to catch those.
            // Same for legacy ~/.codex/skills/.
            if (!SweepCapacitorPrefixedDirs(agents.UserSkillsDir))            hadFailures = true;
            if (!SweepCapacitorPrefixedDirs(harnesses.Of<CodexHarness>().Paths.SkillsDir)) hadFailures = true;

            foreach (var skills in new[] {
                harnesses.Of<KiroHarness>().Paths.SkillsDir,
                harnesses.Of<KiroHarness>().Crew.SkillsDir,
                harnesses.Of<AntigravityHarness>().Paths.SkillsDir,
            }) if (!SweepCapacitorPrefixedDirs(skills)) hadFailures = true;

            if (pathLinks.Count > 0) {
                foreach (var failure in links.Remove()) {
                    await console.Error.WriteLineAsync($"Could not remove {failure.Path}: {failure.Reason}");
                    await console.Error.WriteLineAsync($"Remove it yourself: sudo rm {ShellQuote(failure.Path)}");
                    hadFailures = true;
                }
            }

            if (profiles.Count > 0) {
                foreach (var profile in ShellProfile.Removable(home, [])) {
                    try {
                        ShellProfile.Remove(profile);
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        await console.Error.WriteLineAsync($"Could not remove kcap's PATH line from {profile}: {ex.Message}");
                        hadFailures = true;
                    }
                }
            }

            if (projectRoot is not null) {
                var claudeProject = Path.Combine(projectRoot, ".claude", "settings.local.json");
                var codexProject  = Path.Combine(projectRoot, ".codex", "hooks.json");

                try {
                    var claudeOutcome = PluginCommand.RemoveClaudePlugin(claudeProject);

                    if (claudeOutcome == PluginCommand.ClaudeRemovalOutcome.Removed) {
                        await console.Out.WriteLineAsync($"Plugin removed (project: {claudeProject})");
                    }
                } catch (Exception ex) {
                    await console.Error.WriteLineAsync($"Could not update {claudeProject}: {ex.Message}");
                    hadFailures = true;
                }

                if (File.Exists(codexProject)) {
                    try {
                        if (PluginCommand.RemoveCodexHooks(codexProject)) {
                            await console.Out.WriteLineAsync($"Codex hooks removed (project: {codexProject})");
                        }
                    } catch (Exception ex) {
                        await console.Error.WriteLineAsync($"Could not update Codex hooks at {codexProject}: {ex.Message}");
                        hadFailures = true;
                    }
                }

                // Same marker-survives-after-manual-edit story as the user scope.
                ClaudePluginInstaller.DeleteMarker(claudeProject);
                CodexHooksInstaller.DeleteMarker(codexProject);
            }

            // Holds the daemons' locks and their granted consent. It goes before the config directory, which
            // holds it by default, so a lock still held keeps both. Kept after a failure for the same reason
            // as the registry: a re-run has to find and stop what is still running. A directory
            // KCAP_DAEMONS_DIR moved, or a link, may be shared, so nothing in it is assumed to be kcap's.
            if (!ownsDaemonDir) {
                await console.Out.WriteLineAsync($"Left the daemon state directory {store.Directory} in place: it is moved or linked elsewhere, so kcap cannot tell its files from others'.");
            } else if (!hadFailures && Directory.Exists(store.Directory)) {
                try {
                    if (store.DeleteAll()) {
                        await console.Out.WriteLineAsync($"Removed daemon state directory: {store.Directory}");
                    } else {
                        await console.Error.WriteLineAsync($"Could not remove daemon state directory {store.Directory}: a daemon or daemon command is still using it.");
                        hadFailures = true;
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    await console.Error.WriteLineAsync($"Could not remove daemon state directory {store.Directory}: {ex.Message}");
                    hadFailures = true;
                }
            }

            if (!hadFailures) {
                foreach (var directory in new[] { Path.Combine(home.Path, ".cache", "kcap"), harnesses.Of<CursorHarness>().Paths.SpoolDir }) {
                    try {
                        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                    } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        await console.Error.WriteLineAsync($"Could not remove {directory}: {ex.Message}");
                        hadFailures = true;
                    }
                }
            }

            if (!keepConfig) {
                if (hadFailures) {
                    await console.Error.WriteLineAsync(
                        $"Skipping config-directory delete because earlier steps failed: {configDir}");
                    await console.Error.WriteLineAsync(
                        "Investigate the errors above, then re-run `kcap uninstall` to finish.");
                } else if (Directory.Exists(configDir)) {
                    try {
                        UninstallFiles.DeleteConfig(configDir, localRoot);
                        await console.Out.WriteLineAsync($"Removed configuration from: {configDir}");
                        if (localRoot is not null)
                            await console.Out.WriteLineAsync($"Kept local lifecycle safety metadata in {localRoot} (no recordings).");
                    } catch (Exception ex) {
                        await console.Error.WriteLineAsync($"Could not remove config directory {configDir}: {ex.Message}");
                        hadFailures = true;
                    }
                }
            }

            // The registry is what the plugin removals above read to unwire each account, so it goes last,
            // and only once nothing failed: a re-run needs it to finish the unwiring. It is not part of
            // the config directory, so --keep-config does not spare it.
            if (!hadFailures && Directory.Exists(accountStore.Directory)) {
                try {
                    Directory.Delete(accountStore.Directory, recursive: true);
                    await console.Out.WriteLineAsync($"Removed account registry: {accountStore.Directory}");
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    await console.Error.WriteLineAsync($"Could not remove account registry {accountStore.Directory}: {ex.Message}");
                    hadFailures = true;
                }
            }

            if (hadFailures) {
                await console.Error.WriteLineAsync("kcap uninstall finished with errors — see above.");

                return 1;
            }

            await console.Out.WriteLineAsync("kcap uninstalled.");
            await console.Out.WriteLineAsync("Restart any coding agents that were open during uninstall to clear their loaded kcap hooks.");

            return 0;
        }
    }

    internal const string CancelledLine = "Uninstall cancelled. Nothing was removed.";

    /// <summary>What the plan leaves: uninstall removes what kcap set up, never the program that runs
    /// it, which its installer owns.</summary>
    internal static string KeptProgramLine(InstallKind kind, string? scriptRoot, string? snapName, string? processPath) =>
        kind switch {
            InstallKind.Script when scriptRoot is not null =>
                $"It keeps kcap itself, every installed version under {scriptRoot}. Delete that folder afterwards to remove it.",
            InstallKind.Npm => "It keeps kcap itself. Remove it afterwards with: npm uninstall -g @kurrent/kcap",
            InstallKind.App => "It keeps the kcap desktop app and its command-line tool. Uninstall the app to remove them.",
            InstallKind.Snap when snapName is not null => $"It keeps the kcap snap. Remove it afterwards with: sudo snap remove {snapName}",
            _ when processPath is not null => $"It keeps kcap itself ({processPath}). Remove it the way it was installed.",
            _ => "It keeps kcap itself. Remove it the way it was installed.",
        };

    static string? LocalConsentError(LocalStackState state, string root, bool skipPrompt, bool purgeLocal, bool keepConfig) {
        var interruptedPurge = state is LocalStackState.Interrupted(LocalPurgeJournal.Operation, _);

        if (interruptedPurge && skipPrompt && !purgeLocal) {
            return $"An interrupted purge of the local server in {root} is pending; recovering it would finish deleting the local server's data, whatever --keep-config says. Pass --purge-local to delete it. Nothing was changed.";
        }

        if ((state is LocalStackState.Installed or LocalStackState.Interrupted) && skipPrompt && !purgeLocal && !keepConfig) {
            var subject = state is LocalStackState.Installed { Installation.ServerUrl: var installedUrl }
                ? $"A local server is installed at {installedUrl}"
                : $"An interrupted change to the local server in {root} is pending";

            return $"{subject}; kcap uninstall would delete every session it recorded. Pass --purge-local to delete it, or --keep-config to keep its data.";
        }

        return null;
    }

    static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Deletes every <c>kcap-*</c> directory directly under <paramref name="root"/>, catching skills the
    /// installer's current name list does not know. False when any deletion or the enumeration failed.
    /// </summary>
    bool SweepCapacitorPrefixedDirs(string root) {
        if (!Directory.Exists(root)) return true;

        IEnumerable<string> dirs;
        try {
            dirs = Directory.EnumerateDirectories(root, "kcap-*").ToArray();
        } catch (Exception ex) {
            console.Error.WriteLine($"Could not enumerate {root}: {ex.Message}");
            return false;
        }

        var ok = true;
        foreach (var dir in dirs) {
            try {
                Directory.Delete(dir, recursive: true);
            } catch (Exception ex) {
                console.Error.WriteLine($"Could not remove {dir}: {ex.Message}");
                ok = false;
            }
        }

        return ok;
    }
}

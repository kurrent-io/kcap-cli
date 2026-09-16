using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>Launches the watcher as a real detached child of this process.</summary>
public sealed class ProcessWatcherSpawner(
        ConfigRoot config, ProfileContext profiles, WatcherPaths paths, IProcessStarter starter)
    : IWatcherSpawner {
    // The one URL this process resolved. The request carries no server: a watcher aimed at a
    // different one than the hook that asked for it would stream a session nothing here can see.
    string? Url => profiles.Resolution.ServerUrl;

    public async Task SpawnAsync(WatcherSpawnRequest request) {
        var (key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, vendor) = request;

        // Defence in depth: ShouldSpawnAfter already refuses for an unusable URL, but a caller that
        // bypassed it would otherwise write a PID file asserting capture that cannot happen — a
        // watcher streams to SignalR and can never connect here.
        if (!HookHttp.IsPostable(Url)) {
            await Console.Error.WriteLineAsync(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"watcher not started for {key}"));
            return;
        }

        try {
            var watcherDir = paths.Directory;
            Directory.CreateDirectory(watcherDir);

            var kcapPath = Environment.ProcessPath ?? "kcap";
            // Resolve the long-lived coding-agent PID rather than getppid(): coding
            // agents invoke hooks through a transient executor that dies the moment the
            // hook returns, so by the time the watcher checks IsProcessAlive it sees a
            // dead PID and never starts the monitor task — leaving sessions stuck
            // "active" because session-end is never POSTed. The vendor-aware resolver
            // walks the ppid ancestry to find the agent by name, which is robust to the
            // differing process-group topologies of Claude (transient hook group → bare
            // getpgrp() resolves a dead PID) and Codex (inherits the agent's group).
            var parentPid     = ProcessHelpers.GetCodingAgentPid(vendor);
            var arguments     = BuildSpawnArgs(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, parentPid, vendor);

            var psi = new ProcessStartInfo(kcapPath, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    [ProfileOverrides.UrlVar]    = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };

            // The watcher outlives the hook that spawns it, so it must carry none of the
            // agent's handles with it — an inherited hook-stdout pipe would stay open for the
            // watcher's whole lifetime and the agent's read would never reach EOF.
            if (starter.StartDetached(psi) is not { } pid) {
                await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}");

                return;
            }

            // Line 2 is this incarnation's start-identity token (daemon pid-file layout) so
            // KillWatcher can tell the spawned watcher apart from a later recycle of its pid.
            var token = ProcessStartToken.ForPid(pid);
            await File.WriteAllTextAsync(
                paths.PidFile(key), token is null ? pid.ToString() : $"{pid}\n{token}");

            // This instance's start time, so a later staleness probe knows whether it is still
            // within the startup grace window. Written here rather than by the watcher itself, so
            // it exists even if the child never gets far enough to touch its own heartbeat.
            try {
                WatcherHeartbeat.Touch(paths.StartedFile(key), DateTimeOffset.UtcNow);
            } catch {
                /* best-effort — a missing marker just means IsWatcherAlive treats "now" as startupAt */
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Failed to spawn watcher for {key}: {ex.Message}");
        }
    }

    internal static string BuildSpawnArgs(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride,
            string? cwd,
            bool    skipTitle,
            int?    parentPid,
            string  vendor
        ) {
        var sessionId = sessionIdOverride ?? key;

        var arguments = agentId is not null
            ? $"watch {sessionId} \"{transcriptPath}\" --agent-id {agentId}"
            : $"watch {key} \"{transcriptPath}\"";

        if (cwd is not null) {
            arguments += $" --cwd \"{cwd}\"";
        }

        if (skipTitle) {
            arguments += " --skip-title";
        }

        if (parentPid is { } ppid and > 1) {
            arguments += $" --parent-pid {ppid}";
        }

        arguments += $" --vendor \"{vendor}\"";

        return arguments;
    }
}

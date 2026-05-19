using System.Reflection;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Config;
using Kapacitor.Cli.Daemon.Pty;
using Kapacitor.Cli.Daemon.Pty.Unix;
using Kapacitor.Cli.Daemon.Pty.Windows;
using Kapacitor.Cli.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kapacitor.Cli.Daemon;

public static partial class DaemonRunner {
    public static readonly string LogPath = PathHelpers.ConfigPath("daemon.log");

    /// <summary>
    /// Daemon binary version from <c>[AssemblyInformationalVersion]</c>,
    /// baked at build time by MSBuild's git-info integration. Surfaces on
    /// <c>DaemonConnect</c> so the server's <c>Daemon connected:</c> log
    /// line and <c>DaemonInfo</c> can show "v0.4.11+sha.abc1234".
    /// </summary>
    public static string ResolveDaemonVersion() =>
        typeof(DaemonRunner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public static async Task<int> RunAsync(string[] args) {
        string? logFile = null;
        var     config  = new DaemonConfig();

        // Resolve server URL + active profile. The CLI does this in its own
        // Program.cs, but the daemon is a separate process so its statics start
        // empty. Skips repo discovery (the daemon isn't bound to a working dir);
        // honors --server-url, KAPACITOR_URL, KAPACITOR_PROFILE.
        await AppConfig.ResolveActiveProfile(args);
        config.ServerUrl = AppConfig.ResolvedServerUrl ?? "";

        // CLI arg overrides for daemon-specific settings — parse before host builder.
        // --name is consumed below by DaemonNameResolver (shared with the CLI
        // supervisor), so we don't parse it here.
        for (var i = 0; i < args.Length - 1; i++) {
            switch (args[i]) {
                case "--log-file": logFile = args[++i]; break;
                case "--max-agents" when int.TryParse(args[i + 1], out var n) && n >= 1:
                    config.MaxConcurrentAgents = n;
                    i++;

                    break;
                case "--max-agents":
                    await Console.Error.WriteLineAsync($"Invalid --max-agents value: {args[i + 1]} (must be a positive integer)");

                    return 1;
            }
        }

        // Strip our custom args before passing to host builder
        var hostArgs = Array.Empty<string>();
        var builder  = Host.CreateApplicationBuilder(hostArgs);

        // Configure logging: file when detached, console when foreground
        builder.Logging.ClearProviders();

        if (logFile is not null) {
            builder.Logging.AddProvider(new RollingFileLoggerProvider(logFile));
        } else {
            builder.Logging.AddSimpleConsole(opts => {
                    opts.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                    opts.UseUtcTimestamp = false;
                }
            );
        }

        // Daemon settings from the active profile, with env overrides
        var profileDaemon = AppConfig.ResolvedProfile?.Profile?.Daemon;

        if (config.MaxConcurrentAgents == 5 && profileDaemon is { MaxAgents: var mx and not 5 })
            config.MaxConcurrentAgents = mx;

        if (!string.IsNullOrEmpty(profileDaemon?.ClaudePath))
            config.ClaudePath = profileDaemon.ClaudePath;

        if (!string.IsNullOrEmpty(profileDaemon?.CodexPath))
            config.CodexPath = profileDaemon.CodexPath;

        if (Environment.GetEnvironmentVariable("KAPACITOR_MAX_AGENTS") is { } maxAgents) {
            if (int.TryParse(maxAgents, out var n) && n >= 1)
                config.MaxConcurrentAgents = n;
            else
                await Console.Error.WriteLineAsync($"Warning: ignoring invalid KAPACITOR_MAX_AGENTS={maxAgents}");
        }

        if (Environment.GetEnvironmentVariable("KAPACITOR_CLAUDE_PATH") is { Length: > 0 } envClaudePath)
            config.ClaudePath = envClaudePath;

        if (Environment.GetEnvironmentVariable("KAPACITOR_CODEX_PATH") is { Length: > 0 } envCodexPath)
            config.CodexPath = envCodexPath;

        // Shared name resolution with the CLI supervisor — the CLI's
        // DaemonCommands and the daemon binary must agree on the name so
        // the per-name PID file the CLI inspects is the one the daemon
        // writes via DaemonLock. Resolve throws on `--name <missing value>`
        // / `--name <next-is-flag>`; refuse to start in that case rather
        // than silently defaulting to the OS username.
        try {
            config.Name = DaemonNameResolver.Resolve(args, profileDaemon?.Name);
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var errors = config.Validate();

        if (errors.Count > 0) {
            await Console.Error.WriteLineAsync("Configuration errors:");

            foreach (var e in errors) {
                await Console.Error.WriteLineAsync($"  - {e}");
            }

            return 1;
        }

        // Acquire the per-name flock that prevents another daemon from
        // running under the same name on this machine. The lock content is
        // a fresh instance id that we'll also send over DaemonConnect so
        // the server can refuse a second daemon claiming the same
        // (owner, name) slot (AI-630).
        var daemonLock = DaemonLock.TryAcquire(config.Name);

        if (daemonLock is null) {
            await Console.Error.WriteLineAsync(
                $"Another kapacitor-daemon is already running under the name '{config.Name}' on this machine. "
                + $"Either stop it (`kapacitor daemon stop --name {config.Name}`) or start this one with a different `--name`."
            );

            return 2;
        }

        config.InstanceId = daemonLock.InstanceId;
        config.Version    = ResolveDaemonVersion();

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(daemonLock);
        builder.Services.AddSingleton<ServerConnection>();

        // Local HTTP bridge that fronts the server's permission flow. Registered as a
        // singleton so AgentOrchestrator can read its bound URL at agent-spawn time, AND
        // as a hosted service so its IHostedService lifecycle starts the listener before
        // any agent is spawned.
        builder.Services.AddSingleton<LocalPermissionBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPermissionBridge>());

        if (OperatingSystem.IsWindows()) {
            builder.Services.AddSingleton<IPtyProcessFactory, WinPtyProcessFactory>();
        } else {
            builder.Services.AddSingleton<IPtyProcessFactory, UnixPtyProcessFactory>();
        }

        builder.Services.AddSingleton<WorktreeManager>();
        builder.Services.AddSingleton<RepoMatcher>();

        builder.Services.AddHttpClient("Attachments", client => client.BaseAddress = new Uri(config.ServerUrl));

        builder.Services.AddSingleton<IHostedAgentLauncher, ClaudeLauncher>();
        builder.Services.AddSingleton<IHostedAgentLauncher, CodexLauncher>();

        builder.Services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentLauncher>>(sp =>
            sp.GetServices<IHostedAgentLauncher>().ToDictionary(l => l.Vendor)
        );

        builder.Services.AddSingleton<AgentOrchestrator>();
        builder.Services.AddSingleton<EvalContextCache>();
        builder.Services.AddSingleton<EvalRunner>();

        var host   = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("kapacitor.Daemon");

        // AI-652: probe each registered launcher's CLI binary so the
        // DaemonConnect payload only advertises vendors this daemon can
        // actually spawn. The launch dialog filters its vendor selector
        // by this list. Ordered alphabetically so the wire format is
        // stable across restarts.
        config.SupportedVendors = host.Services.GetServices<IHostedAgentLauncher>()
            .Where(l => l.IsAvailable())
            .Select(l => l.Vendor)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        LogDaemonStarting(logger, config.Name, config.ServerUrl);

        var lifetime   = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var connection = host.Services.GetRequiredService<ServerConnection>();

        // AI-630: if the server rejects our DaemonConnect because another
        // live daemon owns the (owner, name) slot, signal host shutdown
        // and remember to return exit code 3 instead of 0. Subscribe
        // before ConnectAsync so the initial-connect path is covered.
        var nameInUse = false;

        connection.OnNameInUse += _ => {
            nameInUse = true;
            lifetime.StopApplication();
        };

        // Start hosted services (LocalPermissionBridge in particular) BEFORE the SignalR
        // connection comes up. Otherwise an early LaunchAgent message can arrive while
        // BaseUrl is still null and the spawned Claude falls back to the HTTPS path —
        // exactly what this bridge is meant to avoid.
        await host.StartAsync(lifetime.ApplicationStopping);

        try {
            await connection.ConnectAsync(lifetime.ApplicationStopping);
        } catch (Exception ex) when (nameInUse) {
            // ConnectAsync's initial-connect path threw because of
            // NameInUse. OnNameInUse already fired and set our flag; the
            // host hasn't started its main loop yet, so just clean up
            // and exit cleanly with code 3.
            _ = ex;
            daemonLock.Dispose();
            await host.StopAsync();

            return 3;
        }

        var worktreeManager = host.Services.GetRequiredService<WorktreeManager>();
        await worktreeManager.CleanupOrphanedAsync();

        var orchestrator = host.Services.GetRequiredService<AgentOrchestrator>();
        // Instantiate EvalRunner so it wires the per-phase eval handlers
        // (PrepareEval / RunQuestion / FinalizeEval / CancelEval) on the
        // ServerConnection. It's stateless beyond the handler assignment —
        // cached context lives in EvalContextCache — so no disposal dance.
        _ = host.Services.GetRequiredService<EvalRunner>();

        try {
            // Wait without passing the lifetime token: WaitForShutdownAsync(token) treats
            // token cancellation as a fault, so a normal Ctrl+C / lifetime.StopApplication()
            // would surface as OperationCanceledException. The no-arg overload listens
            // internally for ApplicationStopping and returns cleanly.
            await host.WaitForShutdownAsync();
        } finally {
            daemonLock.Dispose();
            await orchestrator.DisposeAsync();
            await connection.DisposeAsync();
            await host.StopAsync();
        }

        // AI-630: if the daemon was shut down because the server told us
        // our (owner, name) slot is contested mid-run (heartbeat-triggered
        // path), exit with code 3 so wrappers (systemd, npm, CI) can tell
        // this apart from a normal Ctrl+C exit.
        return nameInUse ? 3 : 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kapacitor daemon '{Name}' starting, connecting to {ServerUrl}")]
    static partial void LogDaemonStarting(ILogger logger, string name, string serverUrl);
}

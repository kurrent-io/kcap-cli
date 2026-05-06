using kapacitor.Config;
using kapacitor.Daemon.Pty;
using kapacitor.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace kapacitor.Daemon;

public static partial class DaemonRunner {
    public static readonly string LogPath = PathHelpers.ConfigPath("agent.log");

    public static async Task<int> RunAsync(string[] args) {
        string? logFile = null;
        var     config  = new DaemonConfig();

        // Resolve server URL + active profile. The CLI does this in its own
        // Program.cs, but the daemon is a separate process so its statics start
        // empty. Skips repo discovery (the daemon isn't bound to a working dir);
        // honors --server-url, KAPACITOR_URL, KAPACITOR_PROFILE.
        await AppConfig.ResolveActiveProfile(args);
        config.ServerUrl = AppConfig.ResolvedServerUrl ?? "";

        // CLI arg overrides for daemon-specific settings — parse before host builder
        for (var i = 0; i < args.Length - 1; i++) {
            switch (args[i]) {
                case "--name": config.Name = args[++i]; break;
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

        if (string.IsNullOrEmpty(config.Name) && !string.IsNullOrEmpty(profileDaemon?.Name))
            config.Name = profileDaemon.Name;

        if (config.MaxConcurrentAgents == 5 && profileDaemon is { MaxAgents: var mx and not 5 })
            config.MaxConcurrentAgents = mx;

        if (Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_NAME") is { } envName) {
            config.Name = envName;
        }

        if (Environment.GetEnvironmentVariable("KAPACITOR_MAX_AGENTS") is { } maxAgents) {
            if (int.TryParse(maxAgents, out var n) && n >= 1)
                config.MaxConcurrentAgents = n;
            else
                await Console.Error.WriteLineAsync($"Warning: ignoring invalid KAPACITOR_MAX_AGENTS={maxAgents}");
        }

        // Fall back to OS username, then machine name, then a static default
        if (string.IsNullOrEmpty(config.Name)) {
            var userName = Environment.UserName;

            config.Name = !string.IsNullOrEmpty(userName)
                ? userName.ToLowerInvariant()
                : !string.IsNullOrEmpty(Environment.MachineName)
                    ? Environment.MachineName.ToLowerInvariant()
                    : "daemon";
        }

        var errors = config.Validate();

        if (errors.Count > 0) {
            await Console.Error.WriteLineAsync("Configuration errors:");

            foreach (var e in errors) {
                await Console.Error.WriteLineAsync($"  - {e}");
            }

            return 1;
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<ServerConnection>();

        // Local HTTP bridge that fronts the server's permission flow. Registered as a
        // singleton so AgentOrchestrator can read its bound URL at agent-spawn time, AND
        // as a hosted service so its IHostedService lifecycle starts the listener before
        // any agent is spawned.
        builder.Services.AddSingleton<LocalPermissionBridge>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LocalPermissionBridge>());

        if (OperatingSystem.IsWindows()) {
            builder.Services.AddSingleton<IPtyProcessFactory, Pty.Windows.WinPtyProcessFactory>();
        } else {
            builder.Services.AddSingleton<IPtyProcessFactory, Pty.Unix.UnixPtyProcessFactory>();
        }

        builder.Services.AddSingleton<WorktreeManager>();
        builder.Services.AddSingleton<RepoMatcher>();

        builder.Services.AddHttpClient("Attachments", client => client.BaseAddress = new Uri(config.ServerUrl));
        builder.Services.AddSingleton<AgentOrchestrator>();
        builder.Services.AddSingleton<EvalContextCache>();
        builder.Services.AddSingleton<EvalRunner>();

        var host   = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("kapacitor.Daemon");

        LogDaemonStarting(logger, config.Name, config.ServerUrl);

        var lifetime   = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var connection = host.Services.GetRequiredService<ServerConnection>();

        // Start hosted services (LocalPermissionBridge in particular) BEFORE the SignalR
        // connection comes up. Otherwise an early LaunchAgent message can arrive while
        // BaseUrl is still null and the spawned Claude falls back to the HTTPS path —
        // exactly what this bridge is meant to avoid.
        await host.StartAsync(lifetime.ApplicationStopping);

        await connection.ConnectAsync(lifetime.ApplicationStopping);

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
            await orchestrator.DisposeAsync();
            await connection.DisposeAsync();
            await host.StopAsync();
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "kapacitor agent '{Name}' starting, connecting to {ServerUrl}")]
    static partial void LogDaemonStarting(ILogger logger, string name, string serverUrl);
}

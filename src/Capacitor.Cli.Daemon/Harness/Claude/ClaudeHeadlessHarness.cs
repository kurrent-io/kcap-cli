using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>Claude Code headless: every built-in tool off, no MCP servers, the system prompt replaced, and the turn
/// and spend caps the server sends. A schema costs one extra turn, which the server's turn cap already counts.</summary>
sealed partial class ClaudeHeadlessHarness(HarnessRegistry harnesses, DaemonConfig config, TimeProvider time, ILogger<ClaudeHeadlessHarness> logger)
        : IHeadlessHarness {
    public string Harness => "claude";

    public bool IsAvailable() => Executable() is not null;

    /// <summary>The CLI the hosted launches spawn: the configured path, else the default name or the desktop app's CLI.</summary>
    string? Executable() => config.Binaries.Resolve(harnesses.LaunchCommand(HarnessId.Claude, config.ClaudePath));

    public Task<ClaudeCliOutcome> RunAsync(RunHeadlessPromptArgs args, TimeSpan timeout, CancellationToken ct) =>
        ClaudeCliRunner.RunDetailedAsync(args.Prompt, timeout, time, Log, config.Profiles.Resolution.Profile, harnesses,
            model: args.Model, maxTurns: args.MaxTurns, promptViaStdin: true,
            jsonSchema: string.IsNullOrWhiteSpace(args.JsonSchema) ? null : args.JsonSchema,
            maxBudgetUsd: args.MaxBudgetUsd, systemPrompt: args.SystemPrompt,
            executable: Executable() ?? "", ct: ct);

    void Log(string message) => LogRunner(logger, message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "claude headless: {Message}")]
    static partial void LogRunner(ILogger logger, string message);
}

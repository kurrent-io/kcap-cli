using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Codex headless: a read-only sandbox with no network in an empty directory, the system prompt carried as a
/// preamble. Codex has no turn or spend cap, so the deadline is the run's only bound.</summary>
sealed partial class CodexHeadlessHarness(HarnessRegistry harnesses, DaemonConfig config, TimeProvider time, ILogger<CodexHeadlessHarness> logger)
        : IHeadlessHarness {
    public string Harness => "codex";

    public bool IsAvailable() => Executable() is not null;

    /// <summary>The CLI the hosted launches spawn: the configured path, else the default name or the desktop app's CLI.</summary>
    string? Executable() => config.Binaries.Resolve(harnesses.LaunchCommand(HarnessId.Codex, config.CodexPath));

    public Task<ClaudeCliOutcome> RunAsync(RunHeadlessPromptArgs args, TimeSpan timeout, CancellationToken ct) =>
        CodexCliRunner.RunDetailedAsync(args.Prompt, timeout, time, Log, config.Profiles.Resolution.Profile, harnesses,
            model: args.Model, reasoning: string.IsNullOrWhiteSpace(args.ReasoningEffort) ? "low" : args.ReasoningEffort,
            jsonSchema: args.JsonSchema, systemPrompt: args.SystemPrompt,
            executable: Executable() ?? "", ct: ct);

    void Log(string message) => LogRunner(logger, message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "codex headless: {Message}")]
    static partial void LogRunner(ILogger logger, string message);
}

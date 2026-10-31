using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>One harness the daemon can run a headless prompt on, through the runner that locks it down.</summary>
interface IHeadlessHarness {
    /// <summary>The token the server names it by.</summary>
    string Harness { get; }

    /// <summary>Whether its CLI resolves on this host now.</summary>
    bool IsAvailable();

    /// <summary>Runs once, killed at <paramref name="timeout"/> (answering <see cref="ClaudeCliFailure.Timeout"/>);
    /// <paramref name="ct"/> kills it and throws.</summary>
    Task<ClaudeCliOutcome> RunAsync(RunHeadlessPromptArgs args, TimeSpan timeout, CancellationToken ct);
}

using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// What a Codex hook takes from its process, read once so a test can hand each run its own.
/// </summary>
/// <param name="Stdout">What Codex parses as the hook's answer.</param>
/// <param name="Skip">A kcap-launched headless Codex: the hook still answers Codex but records nothing.</param>
/// <param name="HostedAppServer">An envelope-sourced hosted session: the daemon owns its transcript and its approvals.</param>
/// <param name="AutoReview">A hosted app-server session on Codex auto-review, whose approvals never reach the daemon's
/// app-server bridge, so its policy is applied from this hook.</param>
sealed record CodexHookEnvironment(TextWriter Stdout, TextWriter Stderr, bool Skip, bool HostedAppServer, bool AutoReview = false) {
    public static CodexHookEnvironment FromProcess(Func<string, string?> getEnv) =>
        new(Console.Out, Console.Error, getEnv(HostedAgent.SkipVar) is "1", getEnv(HostedAgent.HostedAppServerVar) is "1",
            getEnv(HostedAgent.CodexAutoReviewVar) is "1");
}

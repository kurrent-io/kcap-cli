using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// What a Codex hook takes from its process, read once so a test can hand each run its own.
/// </summary>
/// <param name="Stdout">What Codex parses as the hook's answer.</param>
/// <param name="Skip">A kcap-launched headless Codex: the hook still answers Codex but records nothing.</param>
/// <param name="HostedAppServer">An envelope-sourced hosted session: the daemon owns its transcript and its approvals.</param>
sealed record CodexHookEnvironment(TextWriter Stdout, TextWriter Stderr, bool Skip, bool HostedAppServer) {
    public static CodexHookEnvironment FromProcess(Func<string, string?> getEnv) =>
        new(Console.Out, Console.Error, getEnv(HostedAgent.SkipVar) is "1", getEnv(HostedAgent.HostedAppServerVar) is "1");
}

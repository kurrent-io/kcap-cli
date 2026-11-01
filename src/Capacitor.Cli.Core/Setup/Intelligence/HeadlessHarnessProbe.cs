using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>The harnesses this machine can run headless prompts on, by the daemon's own rule for advertising them: the
/// vendor's CLI, or the desktop app's bundled one, resolves. Stands in until a connected daemon reports its own list,
/// which also honours a configured CLI path.</summary>
public static class HeadlessHarnessProbe {
    public const string Claude = "claude";
    public const string Codex  = "codex";

    public static string[] Detect(HarnessRegistry harnesses, BinaryProbe binaries) => [
        .. new (string Token, HarnessId Id, string Cli)[] {
                (Claude, HarnessId.Claude, ClaudeHarness.CliBinary),
                (Codex, HarnessId.Codex, CodexHarness.CliBinary),
            }
            .Where(h => binaries.Resolve(harnesses.LaunchCommand(h.Id, h.Cli)) is not null)
            .Select(h => h.Token)
            .Order(StringComparer.Ordinal),
    ];
}

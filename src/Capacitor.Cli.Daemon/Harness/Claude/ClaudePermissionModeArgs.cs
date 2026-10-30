using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>The <c>--permission-mode</c> a user's own <c>claude</c> args select, so a recovery that
/// relaunches from the resume ledger can pass it again.</summary>
internal static class ClaudePermissionModeArgs {
    const string Flag = "--permission-mode";

    /// <summary>The last mode named before <c>--</c>, as Claude itself takes it; <c>default</c> is
    /// Claude's older spelling of <see cref="ClaudePermissionModes.Manual"/>.</summary>
    internal static string? Selected(IReadOnlyList<string> args) {
        string? mode = null;

        for (var i = 0; i < args.Count && args[i] != "--"; i++) {
            var arg = args[i];

            if (arg == Flag) {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-')) mode = args[++i];
            } else if (arg.StartsWith(Flag + "=", StringComparison.Ordinal)) {
                mode = arg[(Flag.Length + 1)..];
            }
        }

        return mode == "default" ? ClaudePermissionModes.Manual : mode;
    }
}

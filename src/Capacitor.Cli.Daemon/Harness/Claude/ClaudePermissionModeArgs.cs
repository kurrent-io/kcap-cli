using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>The <c>--permission-mode</c> a user's own <c>claude</c> args select, so a recovery that
/// relaunches from the resume ledger can pass it again.</summary>
internal static class ClaudePermissionModeArgs {
    const string Flag = "--permission-mode";

    /// <summary>The last mode named before <c>--</c>, as Claude itself takes it; <c>default</c> is
    /// Claude's older spelling of <see cref="ClaudePermissionModes.Manual"/>. Without Claude's option
    /// table a mode flag right after a bare option may be that option's value, and reading it as a flag
    /// could recover a session under a looser mode than it ran with, so any such flag makes the answer
    /// <see cref="ClaudePermissionModes.Manual"/>: the most restrictive mode the product offers.</summary>
    internal static string? Selected(IReadOnlyList<string> args) {
        string? mode      = null;
        var     ambiguous = false;

        for (var i = 0; i < args.Count && args[i] != "--"; i++) {
            var     arg = args[i];
            string? value;

            if (arg == Flag) {
                value = i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
            } else if (arg.StartsWith(Flag + "=", StringComparison.Ordinal)) {
                value = arg[(Flag.Length + 1)..];
            } else {
                continue;
            }

            if (i > 0 && args[i - 1].StartsWith('-') && !args[i - 1].Contains('=')) ambiguous = true;
            if (value is null) continue;

            mode = value;
            if (arg == Flag) i++;
        }

        if (ambiguous) return ClaudePermissionModes.Manual;

        return mode == "default" ? ClaudePermissionModes.Manual : mode;
    }
}

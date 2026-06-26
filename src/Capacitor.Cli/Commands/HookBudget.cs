using System.Diagnostics;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-event hook-timeout ceilings (mirror kcap/hooks/hooks.json) and a
/// safety-adjusted "remaining" computed from a process-start timestamp, so the
/// hook always leaves time to spool + exit before Claude's kill.
/// </summary>
public static class HookBudget {
    public static readonly TimeSpan Safety = TimeSpan.FromMilliseconds(1500);

    public static TimeSpan Ceiling(string command) => command switch {
        "session-end" => TimeSpan.FromSeconds(15),
        _             => TimeSpan.FromSeconds(5),
    };

    public static TimeSpan Remaining(long processStartTimestamp, string command) {
        var rem = Ceiling(command) - Stopwatch.GetElapsedTime(processStartTimestamp) - Safety;
        return rem > TimeSpan.Zero ? rem : TimeSpan.Zero;
    }
}

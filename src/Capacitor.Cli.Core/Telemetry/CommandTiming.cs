namespace Capacitor.Cli.Core.Telemetry;

/// <summary>Duration of a command, in milliseconds, from a <see cref="TimeProvider.GetTimestamp"/>
/// reading. Clamped at zero so a clock adjustment can never produce a negative duration in the
/// data.</summary>
public static class CommandTiming {
    public static long ElapsedMs(long startTimestamp, TimeProvider time) {
        var elapsed = time.GetElapsedTime(startTimestamp);

        return Math.Max(0, (long)elapsed.TotalMilliseconds);
    }
}

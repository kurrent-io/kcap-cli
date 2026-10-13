namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>
/// Spacing between watcher cycles. Each consecutive failed cycle doubles the wait up to a ceiling, and a
/// failure is reported only when its reason changes: a lapsed sign-in would otherwise log a line every
/// polling interval for as long as nobody notices it.
/// </summary>
public sealed class GrokBotBackoff(TimeSpan interval, TimeSpan ceiling) {
    int     _failures;
    string? _reason;

    public int ConsecutiveFailures => _failures;

    public TimeSpan NextDelay {
        get {
            if (_failures == 0) return interval;

            var scaled = interval.Ticks * Math.Pow(2, Math.Min(_failures, 30));
            return scaled >= ceiling.Ticks ? ceiling : TimeSpan.FromTicks((long)scaled);
        }
    }

    /// <summary>Records a failed cycle; returns the reason when it should be reported, else null.</summary>
    public string? Failed(string reason) {
        _failures++;
        var report = reason == _reason ? null : reason;
        _reason = reason;
        return report;
    }

    /// <summary>Records a clean cycle; returns how many failed cycles it ended, 0 when there were none.</summary>
    public int Succeeded() {
        var ended = _failures;
        _failures = 0;
        _reason   = null;
        return ended;
    }
}

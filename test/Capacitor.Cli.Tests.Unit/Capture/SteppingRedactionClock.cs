namespace Capacitor.Cli.Tests.Unit.Capture;

/// <summary>A record-budget clock that moves a fixed step on every read.</summary>
sealed class SteppingRedactionClock(TimeSpan step) : TimeProvider {
    long _timestamp;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Add(ref _timestamp, (long)step.TotalMilliseconds);
}

namespace Capacitor.Cli.Tests.Unit.Capture;

/// <summary>A record-budget clock that is frozen, or jumps far past every budget on each read.</summary>
sealed class SwitchableRedactionClock : TimeProvider {
    long _timestamp;
    public volatile bool Exhausted = true;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Exhausted ? Interlocked.Add(ref _timestamp, 1_000_000_000) : Interlocked.Read(ref _timestamp);
}

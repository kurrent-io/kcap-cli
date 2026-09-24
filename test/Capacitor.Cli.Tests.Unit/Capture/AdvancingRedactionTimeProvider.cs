namespace Capacitor.Cli.Tests.Unit.Capture;

sealed class AdvancingRedactionTimeProvider : TimeProvider {
    long _timestamp;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Increment(ref _timestamp);
}

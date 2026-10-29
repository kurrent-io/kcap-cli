namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Where a <see cref="CliTelemetry"/> puts what it captures. Production ships to PostHog via
/// <see cref="TelemetryClient"/>; a disabled facade drops everything; a test keeps the events.
/// </summary>
public interface ITelemetrySink {
    void Enqueue(TelemetryEvent e);

    /// <summary>Ships what is queued. False means nothing reached the far side.</summary>
    Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget);

    /// <summary>Drops everything queued, unsent, when telemetry is turned off mid-run.</summary>
    void Discard();

    /// <summary>Stops this process sending and drops what it queued, but leaves the spool other processes share:
    /// for a stop that is this process's own, not an opt-out.</summary>
    void Abandon() => Discard();
}

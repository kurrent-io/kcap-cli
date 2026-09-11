namespace Capacitor.Cli.Core.Telemetry;

/// <summary>The sink of a facade that is off: it must not queue, ship, or keep anything.</summary>
sealed class NullTelemetrySink : ITelemetrySink {
    public void Enqueue(TelemetryEvent e) { }

    public Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) =>
        Task.FromResult(true);

    public void Discard() { }
}

using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Keeps what a <see cref="CliTelemetry"/> captures instead of shipping it. One per facade, so what
/// a test reads back was written by the facade that test built and by nothing else.
/// </summary>
public sealed class RecordingTelemetrySink : ITelemetrySink {
    readonly List<TelemetryEvent> _events = [];

    public IReadOnlyList<TelemetryEvent> Events {
        get { lock (_events) return [.. _events]; }
    }

    public IReadOnlyList<string> Names {
        get { lock (_events) return [.. _events.Select(e => e.Name)]; }
    }

    /// <summary>How many times the facade asked to ship — a flush with nothing queued still counts.</summary>
    public int Flushes { get; private set; }

    public void Enqueue(TelemetryEvent e) {
        lock (_events) _events.Add(e);
    }

    public Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) {
        Flushes++;

        return Task.FromResult(true);
    }

    public void Discard() {
        lock (_events) _events.Clear();
    }
}

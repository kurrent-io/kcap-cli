using Microsoft.Extensions.Logging;

namespace Capacitor.Tests.Helpers;

/// <summary>An <see cref="ILogger"/> that keeps the rendered warnings, for code whose only visible
/// output on a degraded path is what it logged.</summary>
public sealed class CapturingLogger : ILogger {
    readonly List<string> _warnings = [];

    public IReadOnlyList<string> Warnings { get { lock (_warnings) return [.. _warnings]; } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NoScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
            LogLevel                         logLevel,
            EventId                          eventId,
            TState                           state,
            Exception?                       exception,
            Func<TState, Exception?, string> formatter) {
        if (logLevel != LogLevel.Warning) return;
        lock (_warnings) _warnings.Add(formatter(state, exception));
    }

    sealed class NoScope : IDisposable {
        public static readonly NoScope Instance = new();
        public void Dispose() { }
    }
}

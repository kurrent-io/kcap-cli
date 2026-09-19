using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Capacitor.App.Tests.Unit;

/// Records, as `event:target`, each push a SignalR client failed to hand to a handler: a target
/// with no handler (`MissingHandler`), or arguments that did not bind to the handler's parameters
/// (`ArgumentBindingFailure`, the thrown-and-caught exception).
public sealed class UnboundPushRecorder : ILoggerProvider, ILogger {
    readonly ConcurrentDictionary<string, byte> _failures = new();

    public IReadOnlyCollection<string> Failures => [.._failures.Keys];

    public ILogger CreateLogger(string categoryName) => this;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (eventId.Name is not ("ArgumentBindingFailure" or "MissingHandler")) return;
        if (state is IEnumerable<KeyValuePair<string, object?>> fields
            && fields.FirstOrDefault(f => f.Key is "MethodName" or "Target").Value is string target)
            _failures.TryAdd($"{eventId.Name}:{target}", 0);
    }

    public void Dispose() { }
}

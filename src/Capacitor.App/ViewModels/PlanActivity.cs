using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.ViewModels;

/// What the chat tells the pane's plan section: that the session wrote to its plan, so the pane
/// can re-read the server the moment a write settles instead of waiting out its poll, and whether
/// the session is over.
public sealed class PlanActivity {
    readonly HashSet<string> _writes = new(StringComparer.Ordinal);
    bool _sessionOver;

    /// Raised once per transcript read that settled a write, however many it settled: a replayed
    /// history is a single server read, not one per task the session ever touched.
    public event Action? PlanWritten;
    public event Action? SessionOverChanged;

    public bool SessionOver {
        get => _sessionOver;
        set {
            if (_sessionOver == value) return;
            _sessionOver = value;
            SessionOverChanged?.Invoke();
        }
    }

    public void Apply(IEnumerable<ChatProjectionResult> lines) {
        var written = false;
        foreach (var line in lines)
            foreach (var envelope in line.Envelopes) {
                if (envelope.ToolCallId is not { } callId) continue;
                if (envelope.Kind == AcpEventKind.ToolCall && PlanToolNames.IsWrite(envelope.ToolName)) _writes.Add(callId);
                else if (envelope.Kind == AcpEventKind.ToolResult && _writes.Remove(callId) && !envelope.ToolIsError) written = true;
            }
        if (!written) return;
        // The chat applies this before it builds its own rows, so a throw from a subscriber would
        // cost it the batch for good.
        try {
            PlanWritten?.Invoke();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: plan activity: {ex.Message}");
        }
    }

    public void Clear() => _writes.Clear();
}

using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.ViewModels;

/// What the chat tells the pane's plan section and the Artefacts tab: that the session wrote to its
/// plan or published a page, so they can re-read the server the moment the call settles instead of
/// waiting out the poll, and whether the session is over.
public sealed class PlanActivity {
    readonly HashSet<string> _writes = new(StringComparer.Ordinal);
    readonly HashSet<string> _publishes = new(StringComparer.Ordinal);
    bool _sessionOver;

    /// Raised once per transcript read that settled a write, however many it settled: a replayed
    /// history is a single server read, not one per task the session ever touched.
    public event Action? PlanWritten;
    /// As PlanWritten, for a page publish that succeeded.
    public event Action? PagePublished;
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
        var published = false;
        foreach (var line in lines)
            foreach (var envelope in line.Envelopes) {
                if (envelope.ToolCallId is not { } callId) continue;
                if (envelope.Kind == AcpEventKind.ToolCall) {
                    if (PlanToolNames.IsWrite(envelope.ToolName)) _writes.Add(callId);
                    else if (KcapToolCatalogue.Match(envelope.ToolName)?.Card == ToolCardKind.Page) _publishes.Add(callId);
                } else if (envelope.Kind == AcpEventKind.ToolResult) {
                    if (_writes.Remove(callId) && !envelope.ToolIsError) written = true;
                    if (_publishes.Remove(callId) && !envelope.ToolIsError) published = true;
                }
            }
        if (written) Raise(PlanWritten);
        if (published) Raise(PagePublished);
    }

    /// The chat applies this before it builds its own rows, so a throw from a subscriber would cost
    /// it the batch for good.
    static void Raise(Action? handler) {
        try {
            handler?.Invoke();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: plan activity: {ex.Message}");
        }
    }

    public void Clear() {
        _writes.Clear();
        _publishes.Clear();
    }
}

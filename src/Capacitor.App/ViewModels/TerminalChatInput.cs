using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class TerminalChatInput : ChatInput {
    readonly TerminalTabViewModel _terminal;
    readonly IDisposable _subscription;
    bool _disposed;

    public TerminalChatInput(TerminalTabViewModel terminal) {
        _terminal = terminal;
        _subscription = terminal.WhenAnyValue(t => t.SendAvailability, t => t.State, t => t.CanAcceptText)
            .Skip(1)
            .Subscribe(_ => {
                this.RaisePropertyChanged(nameof(Availability));
                this.RaisePropertyChanged(nameof(CanAcceptText));
                this.RaisePropertyChanged(nameof(Hint));
            });
    }

    public override SendAvailability Availability => _disposed ? SendAvailability.Ended : _terminal.SendAvailability;
    public override bool CanAcceptText => !_disposed && _terminal.CanAcceptText;
    public override string Hint => HintFor(_terminal.SendAvailability, _terminal.State);

    /// The terminal path has nothing to cancel: acceptance is synchronous.
    public override Task<bool> SendAsync(string text, CancellationToken ct) =>
        Task.FromResult(!_disposed && _terminal.TrySendText(text));

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscription.Dispose();
    }

    /// The hint is built from the terminal's own availability, so it is true in the windows where
    /// State alone would lie (a reattach or detach under way while State reads Attached).
    internal static string HintFor(SendAvailability availability, TerminalSessionState state) => availability switch {
        SendAvailability.Ready         => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending       => "Sending…",
        SendAvailability.Transitioning => "Updating the terminal connection…",
        SendAvailability.ReadOnly      => $"Read-only: {state.Detail}",
        SendAvailability.Connecting    => "Connecting to the terminal…",
        SendAvailability.Reattach      => "Reattach the terminal to send",
        SendAvailability.Ended         => "This session has ended",
        _                              => "No terminal to send to",
    };
}

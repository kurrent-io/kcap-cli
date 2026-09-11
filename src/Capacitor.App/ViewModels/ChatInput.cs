using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer's channel to the agent. The terminal channel commits when the paste is accepted; the
/// frame channel when the daemon's ack arrives — that difference lives in SendAsync alone.
public abstract class ChatInput : ReactiveObject, IDisposable {
    public abstract SendAvailability Availability { get; }
    public abstract bool CanAcceptText { get; }
    public abstract string Hint { get; }
    /// Completes when the channel considers the text committed: true to clear the composer.
    public abstract Task<bool> SendAsync(string text, CancellationToken ct);
    public abstract void Dispose();
}

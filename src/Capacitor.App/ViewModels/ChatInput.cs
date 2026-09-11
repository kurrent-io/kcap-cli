using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer's channel to the agent. The terminal channel commits when the paste is accepted; the
/// frame channel when the daemon's ack arrives — that difference lives in SendAsync alone.
public abstract class ChatInput : ReactiveObject, IDisposable {
    public abstract SendAvailability Availability { get; }
    public abstract bool CanAcceptText { get; }
    public abstract string Hint { get; }
    /// Acceptance may clear the composer; an unconfirmed delivery must await transcript evidence.
    public abstract Task<ChatSendOutcome> SendAsync(string text, CancellationToken ct);
    /// The caller has transcript evidence for its latest send. Older receipts must not clear a
    /// newer send's delivery notice.
    public virtual void ConfirmLastSend() { }
    public virtual bool CanInterrupt => false;
    public virtual Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    public abstract void Dispose();
}

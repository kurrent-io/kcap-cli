using Capacitor.App.Services;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer's channel to the agent. The terminal channel commits when the paste is accepted; the
/// frame channel when the daemon's ack arrives — that difference lives in SendAsync alone.
public abstract class ChatInput : ReactiveObject, IDisposable {
    public abstract SendAvailability Availability { get; }
    public abstract bool CanAcceptText { get; }
    public abstract string Hint { get; }
    /// False carries its reason in <see cref="AttachHint"/>.
    public abstract bool CanAttach { get; }
    public abstract string? AttachHint { get; }
    /// The authenticated sender captured by a remote send; null when the channel cannot establish it.
    public virtual string? SenderUserId => null;
    public virtual ViewerIdentityDto? SenderIdentity => SenderUserId is { Length: > 0 } id ? new() { UserId = id } : null;
    /// Acceptance may clear the composer; an unconfirmed delivery must await transcript evidence.
    public abstract Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct);
    /// The caller has transcript evidence for its latest send. Older receipts must not clear a
    /// newer send's delivery notice.
    public virtual void ConfirmLastSend() { }
    public virtual bool CanInterrupt => false;
    public virtual Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    /// Whether a row the server holds can be dropped from its queue through this channel.
    public virtual bool CanDiscardQueued => false;
    public virtual Task<bool> DiscardQueuedAsync(Guid dispatchId, CancellationToken ct) => Task.FromResult(false);
    /// Raw keys in one write, not a paste: a terminal menu reads keystrokes, and bracketed paste would
    /// land as text.
    public virtual Task<bool> SendKeysAsync(byte[] keys, CancellationToken ct) => Task.FromResult(false);
    public virtual bool CanSendKeys => false;
    public abstract void Dispose();
}

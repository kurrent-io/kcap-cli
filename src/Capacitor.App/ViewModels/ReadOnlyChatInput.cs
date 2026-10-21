using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The composer channel of a transcript nobody messages: a subagent's own, read beside its parent.
internal sealed class ReadOnlyChatInput : ChatInput {
    public override SendAvailability Availability => SendAvailability.ReadOnly;
    public override bool CanAcceptText => false;
    public override string Hint => "";
    public override bool CanAttach => false;
    public override string? AttachHint => null;

    public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) =>
        Task.FromResult(ChatSendOutcome.Rejected);

    public override void Dispose() { }
}

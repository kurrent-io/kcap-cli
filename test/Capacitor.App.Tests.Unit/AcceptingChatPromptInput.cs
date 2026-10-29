using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

sealed class AcceptingChatPromptInput(string senderUserId = "u2") : ChatInput {
    public int Sends { get; private set; }
    public override string? SenderUserId => senderUserId;
    public override SendAvailability Availability => SendAvailability.Ready;
    public override bool CanAcceptText => true;
    public override string Hint => "";
    public override bool CanAttach => true;
    public override string? AttachHint => null;
    public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) { Sends++; return Task.FromResult(ChatSendOutcome.Accepted); }
    public override void Dispose() { }
}

using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Local-control ops that answer only the hand-off probe, by default with an attached start on feature/x.
sealed class FakeHandoffProbeOps : ILocalControlOps {
    public const string Sha = "abcdef1234567890abcdef1234567890abcdef12";

    public static readonly HandoffLaunchStart AttachedStart = new(
        "", Sha, nameof(HandoffCheckout.CheckoutBranch), "feature/x", null, nameof(HandoffSource.Branch),
        nameof(HandoffDetachReason.None), null, false, null, null);

    public static HandoffProbeReplyDto Reply(HandoffLaunchStart start, string completeness = "Unconfirmed", IReadOnlyList<string>? missing = null) =>
        new(null, null, start, completeness, missing ?? []);

    public List<HandoffProbeRequestDto> Probes { get; } = [];
    public HandoffProbeReplyDto? Next = Reply(AttachedStart);
    /// When set, a probe answers once the test completes it rather than with Next.
    public TaskCompletionSource<HandoffProbeReplyDto?>? Gate;

    public Task<HandoffProbeReplyDto?> ProbeHandoffAsync(HandoffProbeRequestDto request, CancellationToken ct) {
        Probes.Add(request);
        return Gate is { } gate ? gate.Task : Task.FromResult(Next);
    }

    public Task<StopAgentResult> StopAgentAsync(string agentId, bool force, CancellationToken ct) => throw new NotSupportedException();
    public Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<ConsentAckDto> PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct) => throw new NotSupportedException();
    public Task<ConsentAckDto> PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct) => throw new NotSupportedException();
    public Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct) => throw new NotSupportedException();
    public Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct) => throw new NotSupportedException();
    public Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct) => throw new NotSupportedException();
    public Task<SendTextResult> SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) => throw new NotSupportedException();
    public Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct) => throw new NotSupportedException();
    public Task<SessionRenameAckDto> RenameSessionAsync(SessionRenameDto rename, CancellationToken ct) => throw new NotSupportedException();
    public Task<HandoffOutcomeDto?> ReadHandoffOutcomeAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
}

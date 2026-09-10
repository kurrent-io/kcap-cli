using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using AcpInteractionOption = Capacitor.Remote.Models.AcpInteractionOption;

namespace Capacitor.App.Tests.Unit;

static class PermissionEntries {
    public static PendingPermissionRequest Entry(
            string requestId = "r1", string agentId = "a1", string vendor = "claude", string toolName = "Bash",
            string? toolInputJson = """{"command":"ls"}""", bool omitted = false, string requestedAt = "2026-08-28T10:00:00.0000000+00:00",
            string? toolUseId = null, string? serverRequestId = null) {
        System.Text.Json.JsonElement? input = null;
        if (toolInputJson is not null) { using var d = System.Text.Json.JsonDocument.Parse(toolInputJson); input = d.RootElement.Clone(); }
        return new PendingPermissionRequest(new PermissionPendingDto(requestId, agentId, "s1", vendor, toolName, input, null, omitted, false, requestedAt, toolUseId, serverRequestId));
    }

    public static PendingPermissionRequest Question(
            string requestId = "q1", string agentId = "a1",
            string toolInputJson = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""",
            string requestedAt = "2026-08-28T10:00:00.0000000+00:00") =>
        Entry(requestId, agentId, "claude", ClaudeElicitation.ToolName, toolInputJson, false, requestedAt);

    public static PendingPermissionRequest ServerEntry(
            string requestId = "srv-1", string sessionId = "s1", string vendor = "claude", string toolName = "Bash",
            IReadOnlyList<AcpInteractionOption>? options = null, DateTimeOffset? requestedAt = null) =>
        PendingPermissionRequest.FromServer(new ServerPermissionRequest(sessionId, requestId, toolName, null, options), vendor,
            requestedAt ?? new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero));

    public static PendingPermissionRequest AcpPermission(
            string requestId = "srv-1", string sessionId = "s1", IReadOnlyList<AcpInteractionOption>? options = null,
            DateTimeOffset? requestedAt = null) =>
        ServerEntry(requestId, sessionId, "", "Bash", options ?? [new() { OptionId = "allow", Label = "Allow", Kind = "allow_once" }], requestedAt);

    public static PendingPermissionRequest AcpQuestion(
            string requestId = "q1", string sessionId = "s1", string prompt = "Pick",
            IReadOnlyList<AcpInteractionOption>? options = null, bool isMultiSelect = false,
            DateTimeOffset? requestedAt = null) =>
        PendingPermissionRequest.FromServer(
            new ServerElicitationRequest(sessionId, requestId, prompt,
                options ?? [new() { OptionId = "a", Label = "A" }, new() { OptionId = "b", Label = "B" }], isMultiSelect),
            requestedAt ?? new DateTimeOffset(2026, 8, 28, 10, 0, 0, TimeSpan.Zero));
}

/// Scripted IPermissionService: a real SourceCache plus a per-call outcome queue, like
/// FakeConsentService. A conclusive outcome evicts its target before the caller resumes; a
/// transport failure keeps it.
sealed class FakePermissionService : IPermissionService {
    public readonly SourceCache<PendingPermissionRequest, string> Cache = new(p => p.Key);
    readonly Queue<TaskCompletionSource<PermissionResolveOutcome>> _outcomes = new();
    public readonly List<(string RequestId, PermissionAnswer Answer)> Resolved = [];
    public readonly List<(string RequestId, IReadOnlyList<ElicitationAnswer> Answers)> Answered = [];
    public readonly List<(string RequestId, AcpAnswer Answer)> AcpAnswered = [];
    public readonly List<(string RequestId, string OptionId)> Picked = [];
    public readonly List<string> Withdrawn = [];

    public IObservable<IChangeSet<PendingPermissionRequest, string>> Pending => Cache.Connect();
    public IObservable<int> PendingCount => Cache.CountChanged;
    public IObservable<IReadOnlySet<string>> AgentsWithPending =>
        Cache.Connect().QueryWhenChanged(q => (IReadOnlySet<string>)q.Items.Select(p => p.AgentId).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal))
            .StartWith((IReadOnlySet<string>)new HashSet<string>());
    public IObservable<PendingSummary> Summary =>
        Cache.Connect()
            .QueryWhenChanged(q => PendingSummary.From(q.Items))
            .StartWith(default(PendingSummary));

    public void Add(PendingPermissionRequest entry) => Cache.AddOrUpdate(entry);
    /// Takes the local lane's own id, like every caller's entry builder.
    public void Remove(string requestId) => Cache.Remove(PendingPermissionRequest.KeyFor(PermissionLane.Local, requestId));

    public TaskCompletionSource<PermissionResolveOutcome> Arm() {
        var tcs = new TaskCompletionSource<PermissionResolveOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outcomes.Enqueue(tcs);
        return tcs;
    }
    public void Queue(PermissionResolveKind kind, string? error = null) => Arm().SetResult(new PermissionResolveOutcome(kind, error));

    public async Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct) {
        Resolved.Add((target.RequestId, answer));
        return await SettleAsync(target, "resolve");
    }

    public async Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct) {
        if (target.Questions is null) throw new ArgumentException("not an elicitation entry", nameof(target));
        Answered.Add((target.RequestId, answers));
        return await SettleAsync(target, "answer");
    }

    public async Task<PermissionResolveOutcome> AnswerAcpAsync(PendingPermissionRequest target, AcpAnswer answer, CancellationToken ct) {
        if (target.AcpQuestion is null) throw new ArgumentException("not an ACP question", nameof(target));
        AcpAnswered.Add((target.RequestId, answer));
        return await SettleAsync(target, "ACP answer");
    }

    public async Task<PermissionResolveOutcome> PickOptionAsync(PendingPermissionRequest target, string optionId, CancellationToken ct) {
        if (target.Options?.Any(o => o.OptionId == optionId) != true) throw new ArgumentException("not an offered option", nameof(optionId));
        Picked.Add((target.RequestId, optionId));
        return await SettleAsync(target, "pick");
    }

    public async Task<PermissionResolveOutcome> WithdrawAsync(PendingPermissionRequest target, CancellationToken ct) {
        Withdrawn.Add(target.RequestId);
        return await SettleAsync(target, "withdraw");
    }

    async Task<PermissionResolveOutcome> SettleAsync(PendingPermissionRequest target, string what) {
        if (_outcomes.Count == 0) throw new InvalidOperationException($"FakePermissionService: unscripted {what} call");
        var outcome = await _outcomes.Dequeue().Task;
        if (outcome.Kind != PermissionResolveKind.TransportFailure) Cache.Remove(target.Key);
        return outcome;
    }

    public void Dispose() => Cache.Dispose();
}

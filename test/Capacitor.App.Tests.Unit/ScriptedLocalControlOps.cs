using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

/// Fake ILocalControlOps whose calls are gated by per-call TaskCompletionSources: a test arms the
/// NEXT call's outcome before triggering it, so hold/release is explicit rather than timing-based.
/// A *Calls counter that records a payload is incremented in the same lock as that payload, after
/// the payload is stored and before the gate is taken, so a waiter that observes the counter
/// observes the payload. An already-cancelled token short-circuits the way the real ExchangeAsync
/// does. Callers run on pool threads concurrently; payload properties return snapshots.
sealed class ScriptedLocalControlOps : ILocalControlOps {
    readonly Lock _lock = new();
    readonly Queue<TaskCompletionSource<ConsentPolicyDto>> _gets = new();
    readonly Queue<TaskCompletionSource<ConsentAckDto>> _puts = new();
    readonly Queue<TaskCompletionSource<ConsentAckDto>> _putV2s = new();
    readonly Queue<TaskCompletionSource<StopAgentResult>> _stops = new();
    readonly Queue<TaskCompletionSource<ConsentAckDto>> _resolves = new();
    readonly Queue<TaskCompletionSource<PermissionAckDto>> _permissionResolves = new();
    readonly Queue<TaskCompletionSource<SendTextResult>> _sendTexts = new();
    readonly Queue<TaskCompletionSource<DaemonSettingsAckDto>> _settingsPuts = new();
    readonly List<ConsentPolicyDto> _putPayloads = [];
    readonly List<ConsentPolicyPutV2Dto> _putV2Payloads = [];
    readonly List<(string AgentId, bool Force)> _stopPayloads = [];
    readonly List<ConsentResolveDto> _resolvePayloads = [];
    readonly List<PermissionResolveDto> _permissionResolvePayloads = [];
    readonly List<(string AgentId, string Text)> _sendTextPayloads = [];
    readonly List<(string AgentId, string Text, IReadOnlyList<string> Ids)> _sendTextWithAttachmentsPayloads = [];
    readonly List<DaemonSettingsPutDto> _putSettingsPayloads = [];

    public int GetCalls;
    public int PutCalls;
    public int PutV2Calls;
    public int StopCalls;
    public int ResolveCalls;
    public int PermissionResolveCalls;
    public int SendTextCalls;
    public int SendTextWithAttachmentsCalls;
    public int PutSettingsCalls;
    public Action? SettingsPutStarted;

    public IReadOnlyList<ConsentPolicyDto> PutPayloads => Snapshot(_putPayloads);
    public IReadOnlyList<ConsentPolicyPutV2Dto> PutV2Payloads => Snapshot(_putV2Payloads);
    public IReadOnlyList<(string AgentId, bool Force)> StopPayloads => Snapshot(_stopPayloads);
    public IReadOnlyList<ConsentResolveDto> ResolvePayloads => Snapshot(_resolvePayloads);
    public IReadOnlyList<PermissionResolveDto> PermissionResolvePayloads => Snapshot(_permissionResolvePayloads);
    public IReadOnlyList<(string AgentId, string Text)> SendTextPayloads => Snapshot(_sendTextPayloads);
    public IReadOnlyList<(string AgentId, string Text, IReadOnlyList<string> Ids)> SendTextWithAttachmentsPayloads => Snapshot(_sendTextWithAttachmentsPayloads);
    public IReadOnlyList<DaemonSettingsPutDto> PutSettingsPayloads => Snapshot(_putSettingsPayloads);

    public TaskCompletionSource<ConsentPolicyDto> ArmGet() => Arm(_gets);
    public void QueueGet(ConsentPolicyDto policy) => ArmGet().SetResult(policy);
    public void QueueGetFailure(string reason) => ArmGet().SetException(new LocalControlOpsException(reason, reason));
    public void QueueGetUnmappedFailure(Exception ex) => ArmGet().SetException(ex);

    public TaskCompletionSource<ConsentAckDto> ArmPut() => Arm(_puts);
    public void QueueAck(bool ok, string? error) => ArmPut().SetResult(new ConsentAckDto(ok, error, null));

    public TaskCompletionSource<ConsentAckDto> ArmPutV2() => Arm(_putV2s);
    public void QueuePutV2(bool ok, string? error) => ArmPutV2().SetResult(new ConsentAckDto(ok, error, null));
    public void QueuePutV2Failure(string reason) => ArmPutV2().SetException(new LocalControlOpsException(reason, reason));

    public TaskCompletionSource<StopAgentResult> ArmStop() => Arm(_stops);
    public void QueueStop(StopAgentResult result) => ArmStop().SetResult(result);
    public void QueueStopFailure(string reason) => ArmStop().SetException(new LocalControlOpsException(reason, reason));
    public void QueueStopUnmappedFailure(Exception ex) => ArmStop().SetException(ex);

    public TaskCompletionSource<ConsentAckDto> ArmResolve() => Arm(_resolves);
    public void QueueResolve(bool ok, string? error, bool? ruleSaved = null) => ArmResolve().SetResult(new ConsentAckDto(ok, error, ruleSaved));
    public void QueueResolveFailure(string reason) => ArmResolve().SetException(new LocalControlOpsException(reason, reason));
    public void QueueResolveUnmappedFailure(Exception ex) => ArmResolve().SetException(ex);

    public TaskCompletionSource<PermissionAckDto> ArmPermissionResolve() => Arm(_permissionResolves);
    public void QueuePermissionResolve(bool ok, string? error = null) => ArmPermissionResolve().SetResult(new PermissionAckDto(ok, error));
    public void QueuePermissionResolveFailure(string reason) => ArmPermissionResolve().SetException(new LocalControlOpsException(reason, reason));

    public TaskCompletionSource<SendTextResult> ArmSendText() => Arm(_sendTexts);
    public void QueueSendText(SendTextResult result) => ArmSendText().SetResult(result);

    public TaskCompletionSource<DaemonSettingsAckDto> ArmPutSettings() => Arm(_settingsPuts);
    public void QueuePutSettings(bool ok, string? reason, int? maxAgents) => ArmPutSettings().SetResult(new DaemonSettingsAckDto(ok, reason, maxAgents));
    public void QueuePutSettingsFailure(string reason) => ArmPutSettings().SetException(new LocalControlOpsException(reason, reason));

    public Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct) {
        Interlocked.Increment(ref GetCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ConsentPolicyDto>(ct);
        return Gated(Take(_gets, "Get"), ct);
    }

    public Task<ConsentAckDto> PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct) {
        Record(_putPayloads, policy, ref PutCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ConsentAckDto>(ct);
        return Gated(Take(_puts, "Put"), ct);
    }

    public Task<ConsentAckDto> PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct) {
        Record(_putV2Payloads, put, ref PutV2Calls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ConsentAckDto>(ct);
        return Gated(Take(_putV2s, "PutV2"), ct);
    }

    public Task<StopAgentResult> StopAgentAsync(string agentId, bool force, CancellationToken ct) {
        Record(_stopPayloads, (agentId, force), ref StopCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<StopAgentResult>(ct);
        return Gated(Take(_stops, "Stop"), ct);
    }

    public Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct) {
        Record(_resolvePayloads, resolve, ref ResolveCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<ConsentAckDto>(ct);
        return Gated(Take(_resolves, "Resolve"), ct);
    }

    public Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct) {
        Record(_permissionResolvePayloads, resolve, ref PermissionResolveCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<PermissionAckDto>(ct);
        return Gated(Take(_permissionResolves, "permission resolve"), ct);
    }

    public Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct) {
        Record(_sendTextPayloads, (agentId, text), ref SendTextCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<SendTextResult>(ct);
        return Take(_sendTexts, "SendText").Task.WaitAsync(ct);
    }

    public Task<SendTextResult> SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
        Record(_sendTextWithAttachmentsPayloads, (agentId, text, attachmentIds), ref SendTextWithAttachmentsCalls);
        if (ct.IsCancellationRequested) return Task.FromCanceled<SendTextResult>(ct);
        return Take(_sendTexts, "SendText").Task.WaitAsync(ct);
    }

    /// Answers RenameSession; unset, a rename call throws as any unscripted call does.
    public Func<SessionRenameDto, Task<SessionRenameAckDto>>? Rename;
    public readonly List<SessionRenameDto> RenamePayloads = [];

    public Task<SessionRenameAckDto> RenameSessionAsync(SessionRenameDto rename, CancellationToken ct) {
        RenamePayloads.Add(rename);
        return Rename?.Invoke(rename)
            ?? throw new InvalidOperationException("ScriptedLocalControlOps: unscripted RenameSession call");
    }

    public Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct) {
        Record(_putSettingsPayloads, put, ref PutSettingsCalls);
        SettingsPutStarted?.Invoke();
        if (ct.IsCancellationRequested) return Task.FromCanceled<DaemonSettingsAckDto>(ct);
        return Gated(Take(_settingsPuts, "PutDaemonSettings"), ct);
    }

    TaskCompletionSource<T> Arm<T>(Queue<TaskCompletionSource<T>> queue) {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) queue.Enqueue(tcs);
        return tcs;
    }

    TaskCompletionSource<T> Take<T>(Queue<TaskCompletionSource<T>> queue, string what) {
        lock (_lock) {
            return queue.TryDequeue(out var tcs)
                ? tcs
                : throw new InvalidOperationException($"ScriptedLocalControlOps: unscripted {what} call");
        }
    }

    static Task<T> Gated<T>(TaskCompletionSource<T> tcs, CancellationToken ct) {
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    void Record<T>(List<T> list, T item, ref int counter) {
        lock (_lock) {
            list.Add(item);
            counter++;
        }
    }

    T[] Snapshot<T>(List<T> list) {
        lock (_lock) return [.. list];
    }
}

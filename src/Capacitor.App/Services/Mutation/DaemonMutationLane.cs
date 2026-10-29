using System.Diagnostics;
using System.Text.Json;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.Mutation;

/// The per-verb classification seam the lane's control flow delegates into — kept separate so control flow never has to change when classification rules change.
/// <paramref name="predecessor"/> is the daemon observed just before a reload, null for any other verb.
internal delegate Task<MutationOutcome> MutationClassifier(
    MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
    string? attemptId, ObservedEvidence? predecessor, CancellationToken ct);

/// The app-lifetime singleton every daemon mutation runs through: one owned action at a time,
/// identical concurrent requests coalesce, a different request queues FIFO for its own fresh probe.
public sealed class DaemonMutationLane : IAsyncDisposable {
    // Named so DeliverFaulted's outcome is self-explanatory instead of a bare magic number.
    internal const int UnexpectedExitCode = -1;

    // The one EvidenceFailureLeg sentinel that means "not yet confirmable" rather than "a definite problem".
    const string UnreachableLeg = "unreachable";
    const string PredecessorLeg = "predecessor_still_running";

    internal static readonly TimeSpan DetachedConfirmWindow = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DetachedPollInterval  = TimeSpan.FromSeconds(1);

    readonly DaemonStore _store;
    readonly ILoginShellProbe _shellProbe;
    readonly OutcomeChannel _channel;
    readonly Func<CliPin> _pinnedCli;
    readonly Func<MutationRequest, string?, IKcapCli> _executorFactory;
    readonly Func<MutationRequest, IDaemonObservation> _oneShotFactory;
    readonly TimeProvider _time;
    readonly CancellationTokenSource _lifetime = new();
    // Captured once, before any dispose: _lifetime.Token throws ObjectDisposedException once the
    // source itself is disposed, but a token value obtained earlier stays safely readable/storable.
    readonly CancellationToken _lifetimeToken;

    readonly object _gate = new();
    ActionSlot? _owned;
    readonly List<ActionSlot> _queue = [];
    readonly HashSet<string> _retiredServiceIds = new(StringComparer.Ordinal);
    TaskCompletionSource _quiescent = CompletedSignal();
    bool _disposed;

    // Injectable seam: defaults to the real per-verb classifier below; tests override to pin/observe without running full classification.
    internal MutationClassifier Classify { get; set; }

    internal Func<long, CancellationToken, Task<bool>> AccountsRevisionReached { get; set; } = (_, _) => Task.FromResult(false);

    public DaemonMutationLane(
            DaemonStore store, ILoginShellProbe shellProbe, OutcomeChannel channel,
            Func<CliPin> pinnedCli, Func<MutationRequest, string?, IKcapCli> executorFactory,
            Func<MutationRequest, IDaemonObservation> oneShotFactory, TimeProvider time) {
        _store           = store;
        _shellProbe      = shellProbe;
        _channel         = channel;
        _pinnedCli       = pinnedCli;
        _executorFactory = executorFactory;
        _oneShotFactory  = oneShotFactory;
        _time            = time;
        _lifetimeToken   = _lifetime.Token;
        Classify         = ClassifyOutcomeAsync;
    }

    public async Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken waiterCt) {
        var slot = AttachOrCreate(request);
        try {
            return await slot.Completion.Task.WaitAsync(waiterCt).ConfigureAwait(false);
        } catch (OperationCanceledException) when (waiterCt.IsCancellationRequested) {
            DetachWaiter(slot); // this waiter only — the owned action keeps running under the lane's own token
            throw;
        }
    }

    public bool IsRetired(string daemonName) {
        lock (_gate) return _retiredServiceIds.Contains(DaemonStore.Sanitize(daemonName));
    }

    public async Task<bool> CanRetireAsync(MutationRequest request, CancellationToken ct) {
        var path = await ResolvePathAsync(_pinnedCli(), ct).ConfigureAwait(false);
        if (path is null) return false;
        var cli = _executorFactory(request, path);
        return KcapCliCompatibility.Satisfies(await cli.VersionAsync(ct).ConfigureAwait(false)) &&
            await cli.SupportsServiceRetireAsync(ct).ConfigureAwait(false);
    }

    public async Task QuiescedAsync(CancellationToken ct) {
        while (true) {
            Task wait;
            lock (_gate) {
                if (_owned is null && _queue.Count == 0) return;
                wait = _quiescent.Task;
            }
            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() {
        Task<MutationOutcome>? ownedTask;
        lock (_gate) {
            if (_disposed) return; // idempotent: a second call must not re-cancel/re-dispose the lifetime token
            _disposed = true;

            ownedTask = _owned?.Completion.Task;

            // Cancel every queued waiter up front: shutdown is not an actionable outcome (nothing
            // enqueued to the channel), and draining now means the owned action's own finish has
            // nothing left to admit once it completes. Parameterless TrySetCanceled — _lifetime.Cancel()
            // hasn't run yet at this point, so claiming _lifetime.Token here would let a waiter observe
            // a cancelled task whose token isn't actually cancelled yet.
            foreach (var queued in _queue) queued.Completion.TrySetCanceled();
            _queue.Clear();
        }

        _lifetime.Cancel();

        if (ownedTask is not null) {
            try { await ownedTask.ConfigureAwait(false); } catch { /* terminal disposition already lives on the slot's own TCS */ }
        }

        _lifetime.Dispose(); // only after the drain + owned-action await — no other path can still touch the token
    }

    // --- admission / coalescing ---

    ActionSlot AttachOrCreate(MutationRequest request) {
        ActionSlot slot;
        ActionSlot? toStart = null;
        lock (_gate) {
            if (_disposed) {
                // A disposed lane never spawns: resolve cancelled immediately, nothing owned/queued, nothing enqueued.
                var refused = new ActionSlot(request) { WaiterCount = 1 };
                refused.Completion.TrySetCanceled(_lifetimeToken);
                return refused;
            }

            var existing = _owned is { } owned && owned.Request == request ? owned : _queue.Find(s => s.Request == request);
            if (existing is not null) {
                existing.WaiterCount++;
                return existing;
            }

            slot = new ActionSlot(request) { WaiterCount = 1 };
            if (_owned is null) {
                _owned = slot;
                if (_quiescent.Task.IsCompleted) _quiescent = NewSignal(); // idle -> busy
                toStart = slot;
            } else {
                _queue.Add(slot);
            }
        }
        if (toStart is not null) StartAction(toStart);
        return slot;
    }

    void DetachWaiter(ActionSlot slot) {
        lock (_gate) { slot.WaiterCount--; }
    }

    void StartAction(ActionSlot slot) => _ = RunOwnedAsync(slot);

    async Task RunOwnedAsync(ActionSlot slot) {
        MutationOutcome? outcome = null;
        OperationCanceledException? cancelled = null;
        Exception? fault = null;
        try {
            outcome = await ExecuteActionAsync(slot.Request, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException oce) {
            cancelled = oce;
        } catch (Exception ex) {
            fault = ex;
        }

        if (slot.Request.RetireServiceId is { } retired && outcome is not MutationOutcome.Failed { Reason: "cli_unsupported" }
            && !RenameRefusal.ChangedNothing(outcome)) {
            lock (_gate) _retiredServiceIds.Add(retired);
        }

        // Detach BEFORE resolving the outcome below: closes the window where a fresh RunAsync for
        // the SAME request could coalesce onto a slot whose result is already decided.
        var next = DetachAndAdmitNext(slot);

        if (outcome is not null) Deliver(slot, outcome);
        else if (cancelled is not null) DeliverCancelled(slot, cancelled);
        else DeliverFaulted(slot, fault!);

        // Recursive by construction when actions settle synchronously (fakes, or an already-exited
        // real child) — bounded by queue depth, which stays small in practice; real I/O never does this.
        if (next is not null) StartAction(next);
    }

    ActionSlot? DetachAndAdmitNext(ActionSlot finished) {
        ActionSlot? next = null;
        lock (_gate) {
            Debug.Assert(ReferenceEquals(_owned, finished), "DetachAndAdmitNext: the finishing action must still be the owned slot.");
            _owned = null;
            if (_queue.Count > 0) {
                next = _queue[0];
                _queue.RemoveAt(0);
                _owned = next;
            } else {
                _quiescent.TrySetResult(); // busy -> idle
            }
        }
        return next;
    }

    // The channel feeds the daemon lifecycle surface; an account verb's outcome belongs to the Accounts tab,
    // which presents what its RunAsync returns.
    static bool ReachesChannel(MutationRequest request) => !request.Verb.IsAccountVerb();

    void Deliver(ActionSlot slot, MutationOutcome outcome) {
        var isSuccess = outcome is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout;
        if (!isSuccess && ReachesChannel(slot.Request)) {
            _channel.Enqueue(new OutcomeEnvelope(slot.Request, outcome));
            slot.Completion.TrySetResult(outcome);
            return;
        }
        slot.Completion.TrySetResult(outcome); // resolve waiters FIRST, then decide waiterless — narrows the detach race
        bool waiterless;
        lock (_gate) { waiterless = slot.WaiterCount <= 0; }
        if (waiterless) LogWaiterlessOutcome(slot.Request, outcome);
    }

    // Lane-lifetime cancellation (Dispose) is not an actionable outcome: never enqueued, only logged when nobody is left to observe it directly.
    void DeliverCancelled(ActionSlot slot, OperationCanceledException oce) {
        slot.Completion.TrySetCanceled(oce.CancellationToken);
        bool waiterless;
        lock (_gate) { waiterless = slot.WaiterCount <= 0; }
        if (waiterless) LogWaiterlessCancellation(slot.Request);
    }

    // An unexpected exception during a mutation attempt is actionable evidence, not a fabricated exception surfaced to waiters — the exception itself stays in the log line only.
    void DeliverFaulted(ActionSlot slot, Exception ex) {
        LogUnexpectedFault(slot.Request, ex);
        var outcome = new MutationOutcome.Failed(UnexpectedExitCode, "internal_error", RecoverySurface.Attention);
        if (ReachesChannel(slot.Request)) _channel.Enqueue(new OutcomeEnvelope(slot.Request, outcome));
        slot.Completion.TrySetResult(outcome);
    }

    // --- the owned action itself ---

    async Task<MutationOutcome> ExecuteActionAsync(MutationRequest request, CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); // a disposed lane's cancelled token must stop admission before any spawn

        if (IsRetired(request.DaemonName) || request.RetireServiceId is { } retired && IsRetired(retired))
            return new MutationOutcome.Refused("daemon_renamed_restart_app", RecoverySurface.Attention);

        // Pinned once, before the first await: evidence is always a fresh socket dial, never a live-graph replay.
        var observation = _oneShotFactory(request);

        var pin = _pinnedCli();
        var pinnedPath = await ResolvePathAsync(pin, ct).ConfigureAwait(false);
        if (pinnedPath is null)
            return new MutationOutcome.Refused(pin.OverrideMissing ? "cli_override_missing" : "cli_not_found", RecoverySurface.Attention);

        var executor = _executorFactory(request, pinnedPath); // built ONCE; the same instance runs the probe and the mutation
        var version = await executor.VersionAsync(ct).ConfigureAwait(false);
        if (version is null) return new MutationOutcome.Refused("cli_version_unknown", RecoverySurface.Attention); // the probe did not answer; nothing is known about its age
        if (!KcapCliCompatibility.Satisfies(version)) return new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention);

        // An older CLI ignores --force and the daemon name and runs its all-daemon idle refresh, so the
        // capability is proven on the executable that will run the mutation, before anything is spawned.
        if (request.Verb == MutationVerb.Reload) {
            var status = await executor.ServiceStatusAsync(ct).ConfigureAwait(false);
            // A label that is not loaded reports no spawn type either; that is a state to name, not an old CLI.
            if (status is not null && ServiceStateClassifier.Parse(status.State) == ServiceState.NotInstalled)
                return new MutationOutcome.Refused("not_loaded", RecoverySurface.Attention);
            if (status?.LoadedSpawnType is null) return new MutationOutcome.Refused("reload_unsupported", RecoverySurface.Attention);
        }

        var attemptId = request.Verb == MutationVerb.DetachedStart ? Guid.NewGuid().ToString("N") : null;
        var predecessor = request.Verb == MutationVerb.Reload ? await observation.ObserveAsync(request, ct).ConfigureAwait(false) : null;

        // The runner starts the child before consulting ct, and an already-exited child's own wait
        // can return normally under a cancelled token — this is the last gate before a real spawn.
        ct.ThrowIfCancellationRequested();
        var result = await Dispatch(executor, request, attemptId, ct).ConfigureAwait(false);

        return await Classify(request, result, executor, observation, attemptId, predecessor, ct).ConfigureAwait(false);
    }

    async Task<string?> ResolvePathAsync(CliPin pin, CancellationToken ct) {
        if (pin.OverrideMissing) return null;
        var path = pin.Path ?? await _shellProbe.KcapPathAsync(ct, forceRefresh: false).ConfigureAwait(false);
        // Retry a cached miss so installing the CLI does not require an app restart.
        return path ?? await _shellProbe.KcapPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
    }

    static Task<ProcessResult> Dispatch(IKcapCli executor, MutationRequest request, string? attemptId, CancellationToken ct) =>
        request.Verb switch {
            MutationVerb.Install       => executor.ServiceInstallVerifiedAsync(replace: false, ct),
            MutationVerb.Replace       => executor.ServiceInstallVerifiedAsync(replace: true, ct, request.RetireServiceId),
            MutationVerb.StartVerified => executor.ServiceStartVerifiedAsync(ct),
            MutationVerb.DetachedStart => executor.DetachedStartAsync(attemptId!, ct),
            MutationVerb.Reload        => executor.ServiceReloadAsync(ct),
            MutationVerb.InstallWindowsService => executor.ServiceInstallWindowsServiceAsync(ct),
            var verb when verb.IsAccountVerb() => executor.AccountsAsync(
                verb, request.Account ?? throw new ArgumentException("An account verb carries its AccountArgs.", nameof(request)), ct),
            // Fail closed, never permissive: an unnamed enum value must halt, not silently pick a verb.
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Verb, "unknown MutationVerb"),
        };

    // --- classification ---

    Task<MutationOutcome> ClassifyOutcomeAsync(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
            string? attemptId, ObservedEvidence? predecessor, CancellationToken ct) =>
        request.Verb switch {
            MutationVerb.DetachedStart => ClassifyDetachedStartAsync(request, result, observation, attemptId, ct),
            MutationVerb.Reload        => ClassifyReloadAsync(request, result, executor, observation, predecessor, ct),
            var verb when verb.IsAccountVerb() => ClassifyAccountVerbAsync(request, result, ct),
            _                          => ClassifyServiceVerbAsync(request, result, executor, observation, ct),
        };

    // The CLI reports Succeeded with a failed non-fatal step; the lane counts any failed step as a failure.
    async Task<MutationOutcome> ClassifyAccountVerbAsync(MutationRequest request, ProcessResult result, CancellationToken ct) {
        // A killed run may or may not have committed, whatever it printed; the next snapshot shows which.
        if (result.TimedOut) return new MutationOutcome.Unconfirmed("timeout");
        // The CLI prints a result even for a failed action, so a nonzero exit without one never reached the command.
        if (LastAccountResult(result.Stdout) is not { } dto)
            return result.ExitCode == 0
                ? new MutationOutcome.Unconfirmed("no_result")
                : new MutationOutcome.Failed(result.ExitCode, FirstLine(result.Stderr), RecoverySurface.Attention);

        var failedStep = dto.Steps.FirstOrDefault(s => !s.Succeeded);
        if (result.ExitCode != 0 || !dto.Succeeded || failedStep is not null)
            return new MutationOutcome.Failed(
                result.ExitCode, dto.Error ?? (failedStep is null ? null : $"{failedStep.Name}: {failedStep.Detail}"), RecoverySurface.Attention);

        if (dto.Revision is not { } revision) return new MutationOutcome.Succeeded();

        return await AccountsRevisionReached(revision, ct).ConfigureAwait(false)
            ? new MutationOutcome.Succeeded()
            : new MutationOutcome.Unconfirmed("snapshot_not_seen");
    }

    // Deserialization fills a missing field with its default, so a line without its action or steps is noise, not a result.
    static AccountActionResultDto? LastAccountResult(string stdout) {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--) {
            try {
                if (JsonSerializer.Deserialize(lines[i], AccountActionResultJsonContext.Default.AccountActionResultDto)
                        is { Action: not null, Steps: { } steps } dto && steps.All(s => s is not null))
                    return dto;
            } catch (JsonException) {
                // prose, or a truncated line
            }
        }
        return null;
    }

    // Install/Replace/StartVerified: the CLI's own ServiceVerify transaction engine already
    // performed its readiness poll — exit code alone routes every non-success case.
    static async Task<MutationOutcome> ClassifyServiceVerbAsync(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation, CancellationToken ct) {
        // A forced kill's exit code is not a verify outcome (the transaction may have already
        // committed) — never read as a success OR a coded failure; SucceededAfterTimeout is detached-only.
        if (result.TimedOut) return new MutationOutcome.UnconfirmedNoAttach();

        if (result.ExitCode == 0) return await ClassifyServiceSuccessAsync(request, executor, observation, ct).ConfigureAwait(false);

        if (result.ExitCode == VerifyExitCodes.ElevationDeclined)
            return new MutationOutcome.Refused("elevation_declined", RecoverySurface.Attention);

        if (result.ExitCode == VerifyExitCodes.RetireRefused)
            return new MutationOutcome.Failed(result.ExitCode, ReasonLine.TrySingle(result.Stderr, "retire_reason="), RecoverySurface.Attention);

        if (result.ExitCode == VerifyExitCodes.StartGate) {
            var token = ReasonLine.TrySingle(result.Stderr, "start_gate_reason=");
            return token is null
                ? new MutationOutcome.Failed(VerifyExitCodes.StartGate, null, RecoverySurface.Attention)
                : new MutationOutcome.Failed(VerifyExitCodes.StartGate, token, ReasonRouting.ForStartGate(token));
        }

        if (result.ExitCode == VerifyExitCodes.StartGateDrift) {
            // Drift is never auto-retried — always Attention, regardless of whether a reason line happens to be present.
            var token = ReasonLine.TrySingle(result.Stderr, "start_gate_reason=");
            return new MutationOutcome.Failed(VerifyExitCodes.StartGateDrift, token, RecoverySurface.Attention);
        }

        if (result.ExitCode == VerifyExitCodes.ReadinessTimeout) {
            var token = ReasonLine.TrySingle(result.Stderr, "refusal_reason=");
            return token is null
                ? new MutationOutcome.UnconfirmedNoAttach()
                : new MutationOutcome.Refused(token, ReasonRouting.ForBootRefusal(token));
        }

        return new MutationOutcome.Failed(result.ExitCode, null, RecoverySurface.Attention);
    }

    // Positive evidence only: every leg below must independently hold for Succeeded — any
    // gap degrades to AttentionSkew/AttentionRepair/UnconfirmedNoAttach, never a guessed success.
    static async Task<MutationOutcome> ClassifyServiceSuccessAsync(
            MutationRequest request, IKcapCli executor, IDaemonObservation observation, CancellationToken ct,
            bool requirePositiveSpawnType = false) {
        var evidence  = await observation.ObserveAsync(request, ct).ConfigureAwait(false);
        var ownership = await executor.ServiceStatusAsync(ct).ConfigureAwait(false);

        // Ownership-derived repair/skew signals are evaluated regardless of evidence reachability —
        // a stale marker or an orphaned unit is worth flagging even when the daemon can't be reached.
        if (ownership is not null && OwnershipRepairLeg(ownership) is { } repairLeg)
            return new MutationOutcome.AttentionRepair(repairLeg);

        var evidenceLeg = EvidenceFailureLeg(evidence, request);
        if (evidenceLeg == UnreachableLeg)
            // No independent evidence either way vs. a recorded owner with nothing to show for it are different signals.
            return ownership?.DaemonPid is null
                ? new MutationOutcome.UnconfirmedNoAttach()
                : new MutationOutcome.AttentionSkew("unreachable_with_recorded_owner");
        if (evidenceLeg is not null)
            return new MutationOutcome.AttentionSkew(evidenceLeg);

        if (ownership is null)
            return new MutationOutcome.AttentionSkew("ownership_unknown");

        if (requirePositiveSpawnType) {
            var spawn = ownership.LoadedSpawnType;
            if (SpawnTypes.IsBackgroundBand(spawn)) return new MutationOutcome.AttentionSkew("background_band");
            if (!SpawnTypes.IsPositive(spawn)) return new MutationOutcome.AttentionSkew("spawn_type_unknown");
        }

        if (ownership.JobPid is null || ownership.DaemonPid is null || ownership.JobPid != ownership.DaemonPid)
            return new MutationOutcome.AttentionSkew("ownership_mismatch");

        if (ownership.DaemonPid != evidence!.Pid) // evidenceLeg == null guarantees evidence is non-null and fully valid here
            return new MutationOutcome.AttentionSkew("instance_pid_mismatch"); // same-instance rule

        // Invariant: Succeeded requires a second fresh dial that independently passes full evidence validation AND matches the first's Pid+InstanceId.
        var confirm = await observation.ObserveAsync(request, ct).ConfigureAwait(false);
        var confirmLeg = EvidenceFailureLeg(confirm, request);
        if (confirmLeg is not null)
            return new MutationOutcome.AttentionSkew(confirmLeg);

        if (confirm!.Pid != evidence.Pid || confirm.InstanceId != evidence.InstanceId)
            return new MutationOutcome.AttentionSkew("instance_changed_during_classification");

        return new MutationOutcome.Succeeded();
    }

    // Mirrors DaemonLifecycleController.Reconcile's two orphan-unit predicates plus the stale-marker
    // check — pure signals from ONE ServiceStatusAsync read, independent of any evidence.
    static string? OwnershipRepairLeg(ServiceSnapshot ownership) {
        if (ownership.TxnMarker && !ownership.TxnActive) return "stale_txn_marker";

        var state = ServiceStateClassifier.Parse(ownership.State);
        if (state == ServiceState.Running && ownership.DaemonPid is null) return "running_without_daemon_pid";
        if (ownership.UnitPresent && state == ServiceState.NotInstalled && ownership.DaemonPid is not null) return "daemon_running_outside_service";

        return null;
    }

    // A reload stops the daemon and waits for it to exit before the service brings up a successor, so
    // only a different instance proves it: the one observed beforehand still answering is the old
    // daemon, whatever the CLI's exit code says.
    async Task<MutationOutcome> ClassifyReloadAsync(
            MutationRequest request, ProcessResult result, IKcapCli executor, IDaemonObservation observation,
            ObservedEvidence? predecessor, CancellationToken ct) {
        if (result.TimedOut) return new MutationOutcome.UnconfirmedNoAttach();
        if (result.ExitCode != 0) {
            LogReloadFailure(request, result);
            return new MutationOutcome.Failed(result.ExitCode, ReasonLine.TrySingle(result.Stderr, "refresh_outcome="), RecoverySurface.Attention);
        }

        var restarted = ReasonLine.TrySingle(result.Stderr, "refresh_outcome=") == "reloaded";
        var stale = restarted && predecessor is { Reachable: true, InstanceId: { } old } ? old : null;
        var waited = await AwaitFullEvidenceAsync(request, observation, null, ct, stale).ConfigureAwait(false);
        if (waited is not null) return waited;

        return await ClassifyServiceSuccessAsync(request, executor, observation, ct, requirePositiveSpawnType: true).ConfigureAwait(false);
    }

    // DetachedStart has no CLI-side verify engine — the lane itself confirms via a bounded post-spawn observation window.
    async Task<MutationOutcome> ClassifyDetachedStartAsync(
            MutationRequest request, ProcessResult result, IDaemonObservation observation, string? attemptId, CancellationToken ct) {
        // A forced process-only kill after the wrapper's own bound makes the exit code meaningless — only evidence counts.
        if (result.TimedOut)
            return await AwaitDetachedConfirmationAsync(
                request, observation, attemptId, static () => new MutationOutcome.SucceededAfterTimeout(), ct).ConfigureAwait(false);

        if (result.ExitCode == 0)
            return await AwaitDetachedConfirmationAsync(
                request, observation, attemptId, static () => new MutationOutcome.Succeeded(), ct).ConfigureAwait(false);

        if (result.ExitCode == VerifyExitCodes.DigestGate) {
            var token = ReasonLine.TrySingle(result.Stderr, "daemon_start_reason=");
            return token is null
                ? new MutationOutcome.Failed(VerifyExitCodes.DigestGate, null, RecoverySurface.Attention)
                : new MutationOutcome.Failed(VerifyExitCodes.DigestGate, token, ReasonRouting.ForDaemonStart(token));
        }

        return new MutationOutcome.Failed(result.ExitCode, null, RecoverySurface.Attention);
    }

    // `attemptId` null skips the boot-refusal marker check.
    async Task<MutationOutcome> AwaitDetachedConfirmationAsync(
            MutationRequest request, IDaemonObservation observation, string? attemptId,
            Func<MutationOutcome> onFullEvidence, CancellationToken ct) {
        // Marker-first: a refusing daemon never attaches, so checking the marker before evidence
        // can never produce a false Refused, while evidence-first could let a pre-existing
        // same-name daemon (or a transient mid-boot evidence shape) mask a real refusal.
        MutationOutcome? MarkerCheck() =>
            attemptId is not null && BootRefusalAttribution.TryAttribute(_store, request.DaemonName, attemptId, request.CanonicalServer) is { } refusal
                ? new MutationOutcome.Refused(refusal.Token, ReasonRouting.ForBootRefusal(refusal.Token))
                : null;

        return await AwaitFullEvidenceAsync(request, observation, MarkerCheck, ct).ConfigureAwait(false) ?? onFullEvidence();
    }

    // The one-shot observation never retries, and a daemon that just booted or reloaded needs time to bind
    // its socket. Returns null once evidence is fully positive, else the terminal outcome.
    // `staleInstance` is an instance that must not be the one answering.
    async Task<MutationOutcome?> AwaitFullEvidenceAsync(
            MutationRequest request, IDaemonObservation observation, Func<MutationOutcome?>? preCheck, CancellationToken ct,
            string? staleInstance = null) {
        var deadline = _time.GetUtcNow() + DetachedConfirmWindow;
        while (true) {
            if (preCheck?.Invoke() is { } early) return early;

            var evidence = await observation.ObserveAsync(request, ct).ConfigureAwait(false);
            var leg = EvidenceFailureLeg(evidence, request, staleInstance);
            if (leg is null) return null;

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return leg == UnreachableLeg ? new MutationOutcome.UnconfirmedNoAttach() : new MutationOutcome.AttentionSkew(leg);

            var wait = remaining < DetachedPollInterval ? remaining : DetachedPollInterval;
            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    // The ONE evidence check both service-verb success and DetachedStart confirmation rely on —
    // returns the name of the first failing leg, or null once evidence is fully positive. Structural:
    // pid/instance presence is checked directly, never inferred from the observation's own
    // IdentityConsistent flag (which a broken/legacy adapter could report true without backing it).
    // `staleInstance`, when set, is an instance that must not be the one answering.
    static string? EvidenceFailureLeg(ObservedEvidence? evidence, MutationRequest request, string? staleInstance = null) {
        if (evidence is not { Reachable: true }) return UnreachableLeg;

        if (!ServerIdentity.Matches(evidence.ServerUrl, request.CanonicalServer) || evidence.DaemonName != request.DaemonName)
            return "server_or_name_mismatch";

        if (evidence.Pid is null || evidence.InstanceId is null) return "pre_slice_evidence";

        if (!evidence.IdentityConsistent) return "identity_inconsistent";

        if (evidence.Capabilities is null || !evidence.Capabilities.Contains(ConsentFlipCoordinator.ConsentV3Capability)) return "missing_capability_consent_3";

        if (!KcapCliCompatibility.Satisfies(evidence.DaemonVersion)) return "daemon_below_floor";

        if (staleInstance is not null && evidence.InstanceId == staleInstance) return PredecessorLeg;

        return null;
    }

    static string? FirstLine(string text) {
        foreach (var line in text.AsSpan().EnumerateLines()) {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) return trimmed.ToString();
        }
        return null;
    }

    static void LogReloadFailure(MutationRequest request, ProcessResult result) {
        var detail = result.Stderr.Trim();
        Console.Error.WriteLine(
            $"DaemonMutationLane: reload exit {result.ExitCode} daemon={request.DaemonName}" +
            (detail.Length == 0 ? "" : $": {detail}"));
    }

    static void LogWaiterlessOutcome(MutationRequest request, MutationOutcome outcome) =>
        Console.Error.WriteLine(
            $"DaemonMutationLane: waiterless {outcome.GetType().Name} verb={request.Verb} profile={request.Profile} " +
            $"server={request.CanonicalServer} daemon={request.DaemonName}");

    static void LogWaiterlessCancellation(MutationRequest request) =>
        Console.Error.WriteLine(
            $"DaemonMutationLane: waiterless cancellation (lane lifetime cancel — no envelope) verb={request.Verb} " +
            $"profile={request.Profile} server={request.CanonicalServer} daemon={request.DaemonName}");

    static void LogUnexpectedFault(MutationRequest request, Exception ex) =>
        Console.Error.WriteLine(
            $"DaemonMutationLane: unexpected {ex.GetType().Name} during a mutation attempt verb={request.Verb} " +
            $"profile={request.Profile} server={request.CanonicalServer} daemon={request.DaemonName}: {ex.Message}");

    static TaskCompletionSource CompletedSignal() {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    sealed class ActionSlot(MutationRequest request) {
        public MutationRequest Request { get; } = request;
        public TaskCompletionSource<MutationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaiterCount;
    }
}

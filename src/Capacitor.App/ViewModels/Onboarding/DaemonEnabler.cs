using System.ComponentModel;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One classified row of the daemon matrix — the enabler's whole decision, named.
public enum DaemonRow {
    CliMissing, RequiresSignIn, NoServerConfigured, BinaryUnresolved, StatusUnknown, TransactionActive,
    AlreadyEnabled, OwnedIdentityMismatch, ManualIdentityMatch, ManualIdentityMismatch,
    OrphanLabel, StaleMarker, RunningUnconfirmed, Stopped, NotInstalled,
}

/// The single action a row calls for, or None.
public enum DaemonAffordance { None, Install, Start, Takeover, Repair }

/// <summary>
/// Setup's daemon: named, installed as a service and started once sign-in commits, without a question.
/// Only a service that belongs to another server or profile is replaced on the user's word.
/// Mutations go through the app lane; their outcomes are presented by the channel's single consumer.
/// </summary>
public sealed class DaemonEnabler : ReactiveObject {
    internal const string CliMissingMessage    = "kcap isn't available on this machine.";
    internal const string RequiresSignInMessage = "Sign in before the daemon can be enabled.";
    internal const string NoServerMessage       = "No workspace is configured.";
    internal const string BinaryUnresolvedMessage = "kcap can't find its daemon binary. Reinstall kcap.";
    internal const string StatusUnknownMessage = "Couldn't read the daemon service.";
    internal const string UnrecognizedStateMessage = "The daemon reported an unexpected state.";
    internal const string TxnWaitingMessage    = "Waiting for a daemon operation to finish…";
    internal const string TxnActiveMessage     = "A daemon operation is still running.";
    internal const string AlreadyEnabledMessage = "The daemon is running and will start again after a reboot.";
    internal const string StaleMarkerMessage   = "A previous operation left the service incomplete.";
    internal const string OrphanLabelMessage   = "The service is registered but its files are missing.";
    internal const string RunningUnconfirmedMessage = "The service started, but the daemon hasn't answered yet.";
    internal const string StoppedMessage       = "The daemon is installed but not running.";
    internal const string NotInstalledMessage  = "The daemon service is not installed.";
    internal const string TakeoverDeclinedMessage = "Left as it is — the daemon service was not replaced.";
    internal const string DetachedMessage      = "kcap is still finishing this operation in the background.";
    internal const string ClaimMissingCapabilityMessage =
        "This daemon is too old to accept the consent update — kcap will apply it once the daemon updates.";
    internal const string ClaimFailedMessage =
        "kcap could not update this daemon's consent default — run `kcap daemon consent set-default prompt`, or re-run onboarding.";
    internal const string ClaimAlreadyStricterMessage =
        "This daemon already denies launches by default — kcap left that stricter setting alone.";

    internal static readonly TimeSpan TxnPollInterval = TimeSpan.FromSeconds(2);
    internal const int MaxTxnPolls = 30; // 30 × 2s ≈ the CLI transaction's own 60s worst case

    readonly IKcapCli _cli;
    readonly Func<MutationRequest, CancellationToken, Task<MutationOutcome>> _runMutation;
    readonly Func<(string Profile, string Server, string DaemonName)?> _resolveIdentity;
    readonly IDaemonObservation _observation;
    readonly ILocalControlOps _ops;
    readonly ConsentFlipClaims _claims;
    readonly Func<(string Profile, string Server, string DaemonName)> _resolveIdentityUnderConfigLock;
    readonly ILifecycleSurface _surface;
    readonly Func<CancellationToken, Task<string?>> _terminalPathAsync;
    readonly Func<CancellationToken, Task<string?>> _pickName;
    readonly MachineNameViewModel? _machine;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _single = new(1, 1);

    MutationRequest? _request;
    ObservedEvidence? _evidence;
    string? _replacedName;

    DaemonRow _row = DaemonRow.StatusUnknown;
    DaemonAffordance _affordance;
    bool _busy;
    bool _satisfied;
    string? _message;
    string? _status;

    /// <param name="pickName">The name to save before enabling, or null to keep the configured one.</param>
    public DaemonEnabler(
            IKcapCli cli,
            Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            Func<(string Profile, string Server, string DaemonName)?> resolveIdentity,
            IDaemonObservation observation,
            ILocalControlOps ops,
            ConsentFlipClaims claims,
            Func<(string Profile, string Server, string DaemonName)> resolveIdentityUnderConfigLock,
            ILifecycleSurface surface,
            Func<CancellationToken, Task<string?>> terminalPathAsync,
            TimeProvider time,
            MachineNameViewModel? machine = null,
            Func<CancellationToken, Task<string?>>? pickName = null) {
        _cli                            = cli;
        _runMutation                    = runMutation;
        _resolveIdentity                = resolveIdentity;
        _observation                    = observation;
        _ops                            = ops;
        _claims                         = claims;
        _resolveIdentityUnderConfigLock = resolveIdentityUnderConfigLock;
        _surface                        = surface;
        _terminalPathAsync              = terminalPathAsync;
        _time                           = time;
        _machine                        = machine;
        _pickName                       = pickName ?? (_ => Task.FromResult<string?>(null));
    }

    /// Set ONLY by the lane's own success outcome or the already-enabled row, and never cleared —
    /// a later re-classification must not re-derive (or revoke) a mutation's success from a snapshot.
    public bool Satisfied {
        get => _satisfied;
        private set => this.RaiseAndSetIfChanged(ref _satisfied, value);
    }

    public DaemonRow Row {
        get => _row;
        private set => this.RaiseAndSetIfChanged(ref _row, value);
    }

    public DaemonAffordance Affordance {
        get => _affordance;
        private set => this.RaiseAndSetIfChanged(ref _affordance, value);
    }

    public bool Busy {
        get => _busy;
        private set => this.RaiseAndSetIfChanged(ref _busy, value);
    }

    /// What the current row found.
    public string? Message {
        get => _message;
        private set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    /// The last action's result, mapped from the lane's own outcome — never a re-derived one.
    public string? Status {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// Completes once no <see cref="EnsureAsync"/> is in flight.
    public async Task QuiescedAsync() {
        await _single.WaitAsync().ConfigureAwait(false);
        _single.Release();
    }

    /// What stopped the enabler short of a running service, or null while it runs or once it has.
    public string? Problem => Satisfied || Busy ? null : Status ?? Message;

    /// Names, installs and starts the daemon. Single-flight: a call while one runs waits it out and
    /// then re-checks, so a sign-in that changed the identity mid-run is not missed.
    public async Task EnsureAsync(CancellationToken ct) {
        await _single.WaitAsync(ct).ConfigureAwait(true);
        Busy = true;
        try {
            if (Satisfied) return;
            if (!await SaveNameAsync(ct).ConfigureAwait(true)) return;

            await ClassifyAsync(ct).ConfigureAwait(true);
            if (Satisfied) return;

            await RunAffordanceAsync(ct).ConfigureAwait(true);
        } catch (OperationCanceledException) {
            Status = DetachedMessage;
        } catch (Exception ex) {
            Status = $"The daemon service operation failed unexpectedly: {ex.Message}";
            Console.Error.WriteLine($"kcap: wizard daemon enable failed unexpectedly: {ex.Message}");
        } finally {
            Busy = false;
            _single.Release();
        }
    }

    async Task RunAffordanceAsync(CancellationToken ct) {
        switch (Affordance) {
            case DaemonAffordance.Install:
                await InstallAsync(ct).ConfigureAwait(true);
                break;
            case DaemonAffordance.Start:
                await RunMutationAsync(MutationVerb.StartVerified, ct).ConfigureAwait(true);
                break;
            case DaemonAffordance.Repair:
                await RunMutationAsync(MutationVerb.Replace, ct).ConfigureAwait(true);
                break;
            case DaemonAffordance.Takeover when Row == DaemonRow.ManualIdentityMatch:
                // The same identity outside the service: adopting it changes no one else's daemon.
                await RunMutationAsync(MutationVerb.Replace, ct).ConfigureAwait(true);
                break;
            case DaemonAffordance.Takeover:
                await RunConsentedReplaceAsync(ct).ConfigureAwait(true);
                break;
        }
    }

    /// A name that replaced a configured one retires that name's installed service in the same
    /// transaction. A refused or contended retire changed nothing, so the matrix is read again and run without it:
    /// the saved name still gets its daemon, and the lane presents the refusal with the old service id.
    async Task InstallAsync(CancellationToken ct) {
        if (await RetireTargetAsync(ct).ConfigureAwait(true) is not { } retiring) {
            await RunMutationAsync(MutationVerb.Install, ct).ConfigureAwait(true);
            return;
        }

        var outcome = await RunMutationAsync(MutationVerb.Replace, ct, retiring).ConfigureAwait(true);
        var unchanged = outcome is MutationOutcome.Failed { ExitCode: VerifyExitCodes.RetireRefused or VerifyExitCodes.Contended };
        // Any other failure keeps the name, so a retry retires it if its service is still installed.
        if (unchanged || outcome is MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout) _replacedName = null;
        if (!unchanged) return;

        await ClassifyAsync(ct).ConfigureAwait(true);
        if (!Satisfied) await RunAffordanceAsync(ct).ConfigureAwait(true);
    }

    /// The replaced name's service id, unless its service is known to be absent or the CLI cannot retire
    /// it. An unreadable status still retires: the verify engine checks the old unit itself, skipping an
    /// absent one and refusing a foreign or busy one. A name with nothing to retire is forgotten.
    async Task<string?> RetireTargetAsync(CancellationToken ct) {
        if (_replacedName is not { } replaced) return null;

        var retiring = DaemonStore.Sanitize(replaced);
        var target   = retiring != DaemonStore.Sanitize(_request!.DaemonName)
                    && MayBeInstalled(await _cli.ServiceStatusAsync(replaced, ct).ConfigureAwait(true))
                    && await _cli.SupportsServiceRetireAsync(ct).ConfigureAwait(true);

        if (!target) _replacedName = null;
        return target ? retiring : null;

        static bool MayBeInstalled(ServiceSnapshot? old) =>
            old is null || old.UnitPresent || ServiceStateClassifier.Parse(old.State) is ServiceState.Installed or ServiceState.Running;
    }

    async Task<bool> SaveNameAsync(CancellationToken ct) {
        if (_machine is null || await _pickName(ct).ConfigureAwait(true) is not { } name) return true;

        _machine.DaemonName = name;
        if (await _machine.SaveAsync(ct).ConfigureAwait(true)) {
            _replacedName = _machine.ReplacedName ?? _replacedName;
            return true;
        }

        Status = _machine.Message;
        return false;
    }

    async Task ClassifyAsync(CancellationToken ct) {
        _request  = null;
        _evidence = null;
        Status    = null;

        try {
            if (_cli.CliPath is null) {
                Set(DaemonRow.CliMissing, CliMissingMessage, DaemonAffordance.None);
                return;
            }

            if (_resolveIdentity() is not { } identity) {
                Set(DaemonRow.RequiresSignIn, RequiresSignInMessage, DaemonAffordance.None);
                return;
            }

            // Guarded before any snapshot read — an unbindable server never reaches a mutation.
            if (MutationRequestFactory.TryBuild(
                    MutationVerb.StartVerified, identity.Profile, identity.Server, identity.DaemonName, out var request)
                is MutationOutcome.Refused(var guardReason, _)) {
                Set(DaemonRow.NoServerConfigured, NoServerMessage, DaemonAffordance.None);
                Console.Error.WriteLine($"kcap: wizard daemon enable refused before status: {guardReason}");
                return;
            }

            _request = request!;
            var snapshot = await ReadStatusAsync(ct).ConfigureAwait(true);
            await ClassifySnapshotAsync(snapshot, ct).ConfigureAwait(true);
            WithdrawUnitWritingOfferWithoutBinary(snapshot);
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 2) {
            // A bare "kcap" that is not on PATH throws here. That is a missing CLI, not an unreadable service.
            Set(DaemonRow.CliMissing, CliMissingMessage, DaemonAffordance.None);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            Set(DaemonRow.StatusUnknown, StatusUnknownMessage, DaemonAffordance.None); // unknown, never a positive row
            Console.Error.WriteLine($"kcap: wizard daemon classification failed unexpectedly: {ex.Message}");
        }
    }

    /// A held transaction is waited out, never mutated into. Bounded — an orphaned
    /// grandchild that outlives a force-quit must not wedge setup forever.
    async Task<ServiceSnapshot?> ReadStatusAsync(CancellationToken ct) {
        for (var poll = 0; ; poll++) {
            var snapshot = await _cli.ServiceStatusAsync(ct).ConfigureAwait(true);
            if (snapshot is null || !snapshot.TxnActive || poll >= MaxTxnPolls) return snapshot;

            Message = TxnWaitingMessage;
            await Task.Delay(TxnPollInterval, _time, ct).ConfigureAwait(true);
        }
    }

    // Precedence: unreadable evidence, live transaction, the pid-keyed ownership rows, then the
    // repair rows — a stale marker precedes every install/start row, never a blind reinstall.
    async Task ClassifySnapshotAsync(ServiceSnapshot? snapshot, CancellationToken ct) {
        if (snapshot is null) {
            Set(DaemonRow.StatusUnknown, StatusUnknownMessage, DaemonAffordance.None);
            return;
        }

        var state = ServiceStateClassifier.Parse(snapshot.State);
        if (state == ServiceState.Unknown) {
            Set(DaemonRow.StatusUnknown, UnrecognizedStateMessage, DaemonAffordance.None);
            return;
        }

        if (snapshot.TxnActive) {
            Set(DaemonRow.TransactionActive, TxnActiveMessage, DaemonAffordance.None);
            return;
        }

        if (snapshot.DaemonPid is { } daemonPid) {
            await ClassifyLiveDaemonAsync(snapshot, daemonPid, ct).ConfigureAwait(true);
            return;
        }

        if (state == ServiceState.Installed && !snapshot.UnitPresent) {
            Set(DaemonRow.OrphanLabel, OrphanLabelMessage, DaemonAffordance.Repair);
            return;
        }

        if (snapshot.TxnMarker) {
            Set(DaemonRow.StaleMarker, StaleMarkerMessage, DaemonAffordance.Repair);
            return;
        }

        if (state == ServiceState.Running) {
            Set(DaemonRow.RunningUnconfirmed, RunningUnconfirmedMessage, DaemonAffordance.None);
            return;
        }

        if (snapshot.UnitPresent) {
            Set(DaemonRow.Stopped, StoppedMessage, DaemonAffordance.Start);
            return;
        }

        Set(DaemonRow.NotInstalled, NotInstalledMessage, DaemonAffordance.Install);
    }

    async Task ClassifyLiveDaemonAsync(ServiceSnapshot snapshot, int daemonPid, CancellationToken ct) {
        _evidence = await _observation.ObserveAsync(_request!, ct).ConfigureAwait(true);
        var matched = IdentityMatches(_evidence, _request!);
        var owned   = snapshot.JobPid is not null && snapshot.JobPid == snapshot.DaemonPid;

        if (owned && matched) {
            Set(DaemonRow.AlreadyEnabled, AlreadyEnabledMessage, DaemonAffordance.None);
            Satisfied = true;
            await ApplyPendingClaimAsync(_request!, _evidence).ConfigureAwait(true);
            return;
        }

        if (owned) {
            Set(DaemonRow.OwnedIdentityMismatch,
                $"The installed daemon service is running {DescribeForeign(_evidence)} — replacing it re-points the service at this profile.",
                DaemonAffordance.Takeover);
            return;
        }

        if (matched) {
            // Not enablement: a manual daemon dies at logout and nothing restarts it.
            Set(DaemonRow.ManualIdentityMatch,
                $"A daemon is already running outside the service (PID {daemonPid}).",
                DaemonAffordance.Takeover);
            return;
        }

        Set(DaemonRow.ManualIdentityMismatch,
            $"{DescribeForeign(_evidence)} is already running under this name (PID {daemonPid}).",
            DaemonAffordance.Takeover);
    }

    /// A unit-writing action without a resolvable daemon binary is a guaranteed coded failure, so it
    /// is not attempted. Starting an installed unit writes none.
    void WithdrawUnitWritingOfferWithoutBinary(ServiceSnapshot? snapshot) {
        if (snapshot?.InstallBinaryPath is not null) return;
        if (Affordance is not (DaemonAffordance.Install or DaemonAffordance.Takeover or DaemonAffordance.Repair)) return;

        Set(DaemonRow.BinaryUnresolved, BinaryUnresolvedMessage, DaemonAffordance.None);
    }

    static string DescribeForeign(ObservedEvidence? evidence) =>
        evidence is { Reachable: true, ServerUrl: { Length: > 0 } server }
            ? $"a daemon for {server}"
            : "a daemon kcap could not identify";

    /// Fail closed: a positive match needs a reachable daemon whose two probe dials
    /// provably landed on the SAME process and whose name and canonical server both agree.
    static bool IdentityMatches(ObservedEvidence? evidence, MutationRequest request) =>
        evidence is { Reachable: true, IdentityConsistent: true }
        && evidence.DaemonName == request.DaemonName
        && ServerIdentity.Matches(evidence.ServerUrl, request.CanonicalServer);

    void Set(DaemonRow row, string message, DaemonAffordance affordance) {
        Row        = row;
        Message    = message;
        Affordance = affordance;
    }

    // The ONLY dialog the enabler opens: replacing a service another server or profile owns.
    async Task RunConsentedReplaceAsync(CancellationToken ct) {
        var pathDegraded = await _terminalPathAsync(ct).ConfigureAwait(true) is null;
        var prompt = new LifecyclePrompt(
            LifecyclePrompt.KindTakeover, null, null, pathDegraded, DaemonLifecycleController.TakeoverDisclosure);

        if (!await _surface.ConfirmAsync(prompt, ct).ConfigureAwait(true)) {
            Status = TakeoverDeclinedMessage;
            return;
        }

        await RunMutationAsync(MutationVerb.Replace, ct).ConfigureAwait(true);
    }

    async Task<MutationOutcome> RunMutationAsync(MutationVerb verb, CancellationToken ct, string? retireServiceId = null) {
        var request = _request! with { Verb = verb, RetireServiceId = retireServiceId };
        var outcome = await _runMutation(request, ct).ConfigureAwait(true);

        // The lane's own outcome IS the predicate — never a status snapshot read afterwards.
        Status = OutcomeStatus(verb, outcome);
        if (outcome is not (MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout)) return outcome;

        Satisfied = true;
        _evidence = await _observation.ObserveAsync(request, ct).ConfigureAwait(true);
        if (IdentityMatches(_evidence, request)) await ApplyPendingClaimAsync(request, _evidence).ConfigureAwait(true);

        return outcome;
    }

    /// Reads the live policy first because the put replaces it wholesale, and stays untokened so a
    /// shutdown awaits rather than cancels mid-claim.
    async Task ApplyPendingClaimAsync(MutationRequest request, ObservedEvidence? evidence) {
        try {
            await ApplyPendingClaimCoreAsync(request, evidence).ConfigureAwait(true);
        } catch (Exception ex) {
            Status = Append(Status, ClaimFailedMessage); // claim retained for the post-wizard coordinator
            Console.Error.WriteLine($"kcap: wizard daemon consent flip failed unexpectedly: {ex.Message}");
        }
    }

    async Task ApplyPendingClaimCoreAsync(MutationRequest request, ObservedEvidence? evidence) {
        var pending = await Task.Run(_claims.Pending).ConfigureAwait(true);
        var claim = pending.FirstOrDefault(
            c => c.Profile == request.Profile && c.CanonicalServer == request.CanonicalServer);
        if (claim is null) return;

        if (evidence?.Capabilities?.Contains(ConsentFlipCoordinator.ConsentV3Capability) != true) {
            Status = Append(Status, ClaimMissingCapabilityMessage);
            return;
        }

        ConsentAckDto ack;
        try {
            var policy = await _ops.GetConsentPolicyAsync(CancellationToken.None).ConfigureAwait(true);
            // Seeding respects an operator's deny: it is stricter than prompt, so the flip is inert here.
            if (policy.Default == "deny") {
                Status = Append(Status, ClaimAlreadyStricterMessage);
                return;
            }

            var put = new ConsentPolicyPutV2Dto(request.DaemonName, claim.CanonicalServer, policy with { Default = "prompt" });
            ack = await _ops.PutConsentPolicyV2Async(put, CancellationToken.None).ConfigureAwait(true);
        } catch (LocalControlOpsException) {
            Status = Append(Status, ClaimFailedMessage); // claim retained for the post-wizard coordinator
            return;
        }

        if (!ack.Ok) {
            Status = Append(Status, ClaimFailedMessage); // identity_mismatch or any other rejection
            return;
        }

        await Task.Run(() => _claims.TryConsume(claim, ResolveCanonical, request.DaemonName)).ConfigureAwait(true);
    }

    (string Profile, string Server, string DaemonName) ResolveCanonical() =>
        ConsentFlipClaims.Canonical(_resolveIdentityUnderConfigLock());

    static string Append(string? status, string sentence) =>
        string.IsNullOrEmpty(status) ? sentence : $"{status} {sentence}";

    internal static string OutcomeStatus(MutationVerb verb, MutationOutcome outcome) => outcome switch {
        MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout => "The daemon service is enabled.",
        MutationOutcome.UnconfirmedNoAttach   => $"The daemon {VerbDisplay(verb)} is not yet confirmed — kcap will follow up.",
        MutationOutcome.AttentionSkew(var d)  => $"The daemon {VerbDisplay(verb)} needs attention ({d}) — kcap will follow up.",
        MutationOutcome.AttentionRepair(var d) => $"The daemon {VerbDisplay(verb)} needs attention ({d}) — kcap will follow up.",
        MutationOutcome.Refused(var reason, _) => $"kcap did not change the daemon service ({reason}).",
        MutationOutcome.Failed(var exitCode, var reason, _) =>
            $"The daemon {VerbDisplay(verb)} failed ({reason ?? VerifyExitCodes.Token(exitCode)}).",
        _ => $"The daemon {VerbDisplay(verb)} did not complete.",
    };

    static string VerbDisplay(MutationVerb verb) => verb switch {
        MutationVerb.Install       => "install",
        MutationVerb.Replace       => "replacement",
        MutationVerb.StartVerified => "start",
        MutationVerb.DetachedStart => "start",
        MutationVerb.InstallWindowsService => "Windows service move",
        _                          => verb.ToString(),
    };
}

using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Services;

/// The last Reload outcome that still stands, rendered by the rail block; null stands for "nothing to
/// show". `ExitCode` exists only for a Failed outcome. `Sequence` orders it against passive status reads.
public sealed record ReloadState(ReloadOutcomeKind Kind, string Token, int? ExitCode, string DaemonName, long Sequence) {
    public static ReloadState? From(MutationOutcome outcome, string daemonName, long sequence) => outcome switch {
        MutationOutcome.Succeeded or MutationOutcome.SucceededAfterTimeout => null,
        MutationOutcome.Failed f           => new(ReloadOutcomeKind.Failed, f.Reason ?? VerifyExitCodes.Token(f.ExitCode), f.ExitCode, daemonName, sequence),
        MutationOutcome.Refused r          => new(ReloadOutcomeKind.Refused, r.Reason, null, daemonName, sequence),
        MutationOutcome.AttentionSkew s    => new(ReloadOutcomeKind.Skew, s.Detail, null, daemonName, sequence),
        MutationOutcome.AttentionRepair r  => new(ReloadOutcomeKind.Repair, r.Detail, null, daemonName, sequence),
        MutationOutcome.UnconfirmedNoAttach => new(ReloadOutcomeKind.Unconfirmed, "unconfirmed", null, daemonName, sequence),
        _                                   => new(ReloadOutcomeKind.Failed, outcome.GetType().Name, null, daemonName, sequence),
    };
}

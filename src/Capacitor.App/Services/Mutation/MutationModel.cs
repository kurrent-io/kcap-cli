using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Mutation;

public enum MutationVerb {
    Install, Replace, StartVerified, DetachedStart, Reload, AccountAdd, AccountRemove, AccountRename, AccountRewire, AccountRefresh,
    InstallWindowsService
}

public static class MutationVerbExtensions {
    public static bool IsAccountVerb(this MutationVerb verb) =>
        verb is MutationVerb.AccountAdd or MutationVerb.AccountRemove or MutationVerb.AccountRename or MutationVerb.AccountRewire
            or MutationVerb.AccountRefresh;
}

public sealed record MutationRequest(
    MutationVerb Verb, string Profile, string CanonicalServer, string DaemonName, string? RetireServiceId = null,
    AccountArgs? Account = null, string? RequestId = null);

/// The mutation lane maps every raw outcome onto exactly one case.
public abstract record MutationOutcome {
    public sealed record Succeeded : MutationOutcome;
    public sealed record SucceededAfterTimeout : MutationOutcome;
    public sealed record AttentionSkew(string Detail) : MutationOutcome;
    public sealed record AttentionRepair(string Detail) : MutationOutcome;
    public sealed record UnconfirmedNoAttach : MutationOutcome;
    public sealed record Unconfirmed(string Detail) : MutationOutcome;
    public sealed record Refused(string Reason, RecoverySurface Surface) : MutationOutcome;
    public sealed record Failed(int ExitCode, string? Reason, RecoverySurface Surface) : MutationOutcome;
}

public sealed record OutcomeEnvelope(MutationRequest Request, MutationOutcome Outcome);

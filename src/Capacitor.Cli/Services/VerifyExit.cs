namespace Capacitor.Cli.Services;

/// <summary>Coded, stable exit codes for the <c>--verify</c> transaction engines.
/// Every non-<see cref="Ok"/> member has a matching stderr token defined beside it.</summary>
public static class VerifyExit {
    public const int Ok = 0;

    /// <summary>Service lock contended (start/install), OR install's pre-mutation probe found the
    /// label already Loaded — a fresh install without <c>--replace</c> must not clear it.</summary>
    public const int Contended = 20;
    public const string ContendedToken = "verify_contended";

    /// <summary>Pre-mutation viability failed before anything on disk was touched: the daemon binary
    /// is missing, the pinned profile does not resolve to a valid http/https server URL, or the plist
    /// could not be rendered (e.g. an XML-unrepresentable captured env value). Nothing is touched.</summary>
    public const int Viability = 21;
    public const string ViabilityToken = "verify_viability";

    /// <summary>Install-verify's pre-mutation probe classified the label <c>Unknown</c> — neither
    /// clearly loaded nor clearly absent, so a fresh install refuses to guess and writes nothing.</summary>
    public const int BootoutUnknown = 22;
    public const string BootoutUnknownToken = "verify_bootout_unknown";

    /// <summary>Either <c>--replace</c>'s takeover kill couldn't confirm the owner gone, or an owning
    /// takeover's cleared label never released the daemon name (its validated pid never went null),
    /// within the forward budget. Nothing is written; the marker is retained at its last phase.</summary>
    public const int StopUnconfirmed = 23;
    public const string StopUnconfirmedToken = "verify_stop_unconfirmed";

    /// <summary>Forward cutoff hit and rollback restored the verified-safe state (start: unloaded,
    /// plist retained; install: label absent AND unit file removed).</summary>
    public const int ReadinessTimeout = 24;
    public const string ReadinessTimeoutToken = "verify_readiness_timeout";

    /// <summary>Install-verify's hello was well-formed but reported a wrong name/protocol/version — a
    /// deterministic mismatch, so rollback fires without waiting out the forward budget.</summary>
    public const int HelloValidation = 25;
    public const string HelloValidationToken = "verify_hello_validation";

    /// <summary>A rollback poll ran out with the state genuinely undetermined — the LAST observation
    /// was <see cref="LabelProbe.Unknown"/>, so there is no way to tell whether the restore happened.
    /// A timeout, not an observed-wrong state (compare <see cref="RestoreVerification"/>). The marker
    /// is retained.</summary>
    public const int RollbackBudget = 26;
    public const string RollbackBudgetToken = "verify_rollback_budget";

    /// <summary>Rollback (or the final recheck) affirmatively found the wrong state: still Loaded,
    /// unloaded but the unit file still present, or a plist whose fingerprint doesn't match what this
    /// transaction wrote (a foreign writer). The marker, and any foreign file, are left alone.</summary>
    public const int RestoreVerification = 27;
    public const string RestoreVerificationToken = "verify_restore_verification";

    /// <summary>start --verify's pre-mutation consent-directive gate refused to
    /// proceed: the invoking launcher set <c>KCAP_CONSENT_SEED_DEFAULT</c> but the installed unit's
    /// own baked directive, binary digest, or identity evidence didn't satisfy the gate. Fires right
    /// after the fresh query and BEFORE the marker is even written — nothing is touched. The stderr
    /// line <c>start_gate_reason=&lt;reason&gt;</c> names which check failed.</summary>
    public const int StartGate = 28;
    public const string StartGateToken = "verify_start_gate";

    /// <summary>The gated start path detected drift between the evidence Phase A gated on and what
    /// bootstrap was about to act on — the unit's plist content changed, or its digest stopped
    /// matching, in the window between the gate check and the boot-out/bootstrap it authorizes.
    /// Rolled back the same way a forward-phase failure is (bootout, plist retained).</summary>
    public const int StartGateDrift = 29;
    public const string StartGateDriftToken = "verify_start_gate_drift";

    /// <summary><c>--retire</c> refused to remove the named unit: its plist is unreadable, or it is
    /// not pinned to the profile being installed; or the new id is already taken by an installed unit,
    /// or its state is unknown. No unit is written for the new id — its own entry-time leftover-marker
    /// recovery already ran, as on every install. Every refusal but <c>target_claimed</c> leaves both
    /// units untouched and writes nothing; <c>target_claimed</c> (the new id was taken while the old unit
    /// was being retired) leaves the old unit retired and a captured marker for the new id. The stderr line
    /// <c>retire_reason=&lt;reason&gt;</c> names which.</summary>
    public const int RetireRefused = 30;
    public const string RetireRefusedToken = "verify_retire_refused";
}

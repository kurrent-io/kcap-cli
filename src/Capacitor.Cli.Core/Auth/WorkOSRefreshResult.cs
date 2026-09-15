namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// How a WorkOS refresh resolved once its in-window replays are spent. WorkOS rotates the refresh
/// token on use, so the outcome must separate a token it <see cref="Rejected"/> — consumed or refused,
/// repair is a fresh login — from a <see cref="TransportFailed"/> where no reply ever arrived and the
/// same token may still be live for a later attempt.
/// </summary>
public enum WorkOSRefreshOutcome {
    Rotated,
    Rejected,
    TransportFailed
}

/// <summary>The result of <see cref="WorkOSClient.RefreshAsync"/>; <see cref="Response"/> is set only when <see cref="WorkOSRefreshOutcome.Rotated"/>.</summary>
public readonly record struct WorkOSRefreshResult(WorkOSRefreshOutcome Outcome, WorkOSAuthResponse? Response);

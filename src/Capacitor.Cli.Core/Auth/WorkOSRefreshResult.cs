namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// How a WorkOS refresh resolved. WorkOS rotates the refresh token single-use, so the outcome must
/// separate a token it <see cref="Rejected"/> — consumed or refused, repair is a fresh login — from a
/// <see cref="TransportFailed"/> that never reached it, where the same token is still live to try again.
/// </summary>
public enum WorkOSRefreshOutcome {
    Rotated,
    Rejected,
    TransportFailed
}

/// <summary>The result of <see cref="WorkOSClient.RefreshAsync"/>; <see cref="Response"/> is set only when <see cref="WorkOSRefreshOutcome.Rotated"/>.</summary>
public readonly record struct WorkOSRefreshResult(WorkOSRefreshOutcome Outcome, WorkOSAuthResponse? Response);

using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Abstracts the one action the proactive token-refresh tick performs, so the loop can be
/// unit-tested without a real token store or network. Implemented in production by
/// <see cref="TokenStoreRefreshPort"/>.
/// </summary>
internal interface IProactiveTokenRefreshPort {
    /// <summary>
    /// Refresh the active profile's token if it is within the expiry window. See
    /// <see cref="ProactiveRefreshOutcome"/> for the meaning of each result.
    /// </summary>
    Task<ProactiveRefreshOutcome> RefreshIfExpiringAsync();
}

/// <summary>
/// Production port — proactively refreshes the active profile's token through the shared,
/// cross-process-locked <see cref="TokenStore"/> when it is within <paramref name="window"/>
/// of expiry.
/// </summary>
internal sealed class TokenStoreRefreshPort(TimeSpan window) : IProactiveTokenRefreshPort {
    public Task<ProactiveRefreshOutcome> RefreshIfExpiringAsync() => TokenStore.RefreshIfExpiringAsync(window);
}

/// <summary>
/// Daemon-driven proactive auth-token refresh (AI-992). Auth tokens are otherwise refreshed
/// lazily — only once a hook hits an expired access token — so after an idle period the user
/// sees a 401 and must re-run <c>kcap login</c>. This loop keeps the active profile's token
/// warm by refreshing it <em>ahead</em> of expiry while the daemon runs, which for WorkOS's
/// sliding inactivity window pushes forced re-logins out to the absolute session lifetime.
///
/// The heavy lifting (window check, provider gating, cross-process lock, rotation-safe
/// re-read) lives in <see cref="TokenStore.RefreshIfExpiringAsync"/>; this class drives it,
/// reports the outcome, and rate-limits attempts.
///
/// <para><b>Rate limiting.</b> A refresh ATTEMPT (success or failure) arms a
/// <c>minAttemptInterval</c> gate; ticks inside that gate are skipped without touching the
/// endpoint. This bounds refresh traffic in the cases where the token keeps landing back
/// inside the window every tick — a refresh that fails (dead / rotated refresh token, server
/// down) leaves the expiry unchanged, and a provider that issues a token whose whole lifetime
/// is shorter than the window (or <see cref="TokenStore.JwtExpiry"/>'s parse-failure fallback)
/// puts the freshly-refreshed token right back inside it. A no-op (NotDue) tick is not an
/// attempt and never arms the gate, so a healthy long-lived token is still refreshed promptly
/// the moment it enters the window.</para>
/// </summary>
internal sealed class TokenRefreshLoop(
        IProactiveTokenRefreshPort port,
        ILogger                    logger,
        TimeSpan                   minAttemptInterval,
        Func<DateTimeOffset>?      utcNow = null
    ) {
    readonly Func<DateTimeOffset> _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);

    // Earliest time at which the next refresh ATTEMPT may run; advanced after every attempt.
    DateTimeOffset _nextAttemptAllowedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Total — never throws (modulo outer cancellation, which is expected at shutdown). The
    /// loop in <c>AgentOrchestrator.RunTokenRefreshLoopAsync</c> runs as an unobserved
    /// background Task; if a tick faulted the loop would die silently and proactive refresh
    /// would stop, letting the session lapse after the next idle gap.
    /// </summary>
    public async Task TickAsync(CancellationToken ct) {
        try {
            // Skip without hitting the endpoint while the post-attempt gate is still closed.
            if (_utcNow() < _nextAttemptAllowedAt) {
                return;
            }

            switch (await port.RefreshIfExpiringAsync()) {
                case ProactiveRefreshOutcome.Refreshed:
                    _nextAttemptAllowedAt = _utcNow() + minAttemptInterval;
                    logger.LogDebug("Proactive token refresh: token inside the expiry window — refreshed ahead of expiry");

                    break;

                case ProactiveRefreshOutcome.Failed:
                    _nextAttemptAllowedAt = _utcNow() + minAttemptInterval;

                    logger.LogWarning(
                        "Proactive token refresh failed — backing off for {BackoffSeconds:F0}s; run `kcap login` if this persists",
                        minAttemptInterval.TotalSeconds
                    );

                    break;

                case ProactiveRefreshOutcome.Contended:
                    // A peer (hook/watcher/MCP) held the cross-process refresh lock — it is
                    // presumably refreshing. No endpoint call was made, so this is not a failure:
                    // stay quiet and retry on the next tick (no warning, no backoff).
                    logger.LogDebug("Proactive token refresh: another process holds the refresh lock — retrying next tick");

                    break;

                case ProactiveRefreshOutcome.NotDue:
                    logger.LogTrace("Proactive token refresh: token still valid — nothing to do");

                    break;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Outer cancellation (process shutting down) — let the loop exit.
        } catch (Exception ex) {
            logger.LogWarning(ex, "Proactive token refresh tick faulted — continuing loop");
        }
    }
}

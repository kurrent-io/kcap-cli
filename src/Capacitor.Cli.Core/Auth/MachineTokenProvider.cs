using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Auth;

/// <summary>WorkOS <c>client_credentials</c> token response.</summary>
public record MachineTokenResponse {
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }
}

/// <summary>Outcome of a mint: exactly one of the two is non-null.</summary>
public readonly record struct MachineTokenResult(string? Token, string? Problem);

/// <summary>
/// Exchanges a machine credential for a short-lived bearer, and holds it IN MEMORY ONLY.
///
/// <para><b>Never the token store.</b> A machine token is minted from a credential the runner already
/// has, so caching it on disk buys nothing and costs the property the whole design rests on — that a
/// machine's bearer exists only for the life of the process that needs it. It also has no refresh
/// token: <c>client_credentials</c> returns an access token and nothing else, so "refresh" here means
/// "mint another", which needs only the credential.</para>
///
/// <para><b>Single-flight.</b> One process can build many clients (hooks, the watcher, MCP servers), so
/// the mint is serialised behind a semaphore and the result shared. Without it a burst of concurrent
/// callers would each mint a token — WorkOS would allow it, but it is pure waste and makes the token a
/// moving target while debugging.</para>
///
/// <para><b>The failure reason is RETURNED, never parked on a static.</b> The daemon makes concurrent
/// calls, so a shared field lets one caller report another's reason, or lets a success clear a failure
/// the other has not read yet. It also avoids a memory-model trap that the cache below has to handle
/// explicitly: a <c>SemaphoreSlim</c> release is not a barrier, so a static written inside the gate is
/// not guaranteed visible to a reader outside it.</para>
/// </summary>
public static class MachineTokenProvider {
    /// <summary>
    /// Re-mint this long before nominal expiry. A token that expires mid-flight surfaces as a 401 the
    /// caller must interpret; spending a few seconds of a 3600s lifetime avoids that entirely.
    /// </summary>
    internal static readonly TimeSpan RenewMargin = TimeSpan.FromSeconds(60);

    static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A minted token with the identity and deadline that decide whether it can be reused.</summary>
    sealed record CachedToken(string Token, string ClientId, string TokenUrl, DateTimeOffset Expiry);

    // One reference, published atomically: the lock-free read below could otherwise see a torn
    // combination of separate fields. A Gate release is not a memory barrier, so what makes that read
    // sound is the Volatile.Write/Volatile.Read pairing rather than the semaphore.
    static CachedToken? cached;

    /// <summary>Test seam — the cache is process-wide static state.</summary>
    internal static void ResetForTesting() => Volatile.Write(ref cached, null);

    /// <summary>
    /// Returns a usable bearer for <paramref name="credential"/>, minting one if the cache is empty, keyed
    /// to a different credential or endpoint, near expiry, or holding the token the server just rejected.
    ///
    /// <para><paramref name="rejectedToken"/> is how a 401 becomes a re-mint: the caller passes back the
    /// token the server refused, and if that is what is cached it is discarded before the check. Without
    /// this a revoked-then-reissued credential would keep serving the dead token until its clock ran
    /// out.</para>
    ///
    /// <para>The cache is keyed on client id AND token URL: without that, a process using two
    /// credentials, or one credential against two endpoints, serves the first token to the second
    /// caller.</para>
    ///
    /// <para>Never throws: this runs on the client-construction path, whose contract is to report an auth
    /// outcome rather than explode — a hook that cannot authenticate must exit quietly, not stack-trace
    /// into a transcript.</para>
    /// </summary>
    public static async Task<MachineTokenResult> GetTokenAsync(
            WorkOSClient      workos,
            MachineCredential credential,
            string?           rejectedToken,
            CancellationToken ct
        ) {
        var tokenUrl = MachineAuth.TryResolveTokenUrl(out var urlProblem);

        if (tokenUrl is null) return new(null, urlProblem);

        // A hit must not take the Gate. It is one process-wide semaphore, so checking the cache behind
        // it queues every caller in the process against whichever one happens to be minting. A rejection
        // has to evict, which is a write, so it goes the long way round.
        if (rejectedToken is null
            && Reusable(Volatile.Read(ref cached), credential, tokenUrl) is { } hit) return new(hit, null);

        await Gate.WaitAsync(ct);

        try {
            var snapshot = Volatile.Read(ref cached);

            if (rejectedToken is not null
                && snapshot is not null
                && string.Equals(snapshot.Token, rejectedToken, StringComparison.Ordinal)) {
                snapshot = null;
                Volatile.Write(ref cached, null);
            }

            // Re-check: a mint that finished while this call waited has already published a token.
            if (Reusable(snapshot, credential, tokenUrl) is { } fresh) return new(fresh, null);

            var (token, expiresIn, problem) = await workos.MintAsync(credential, tokenUrl, ct);

            if (token is null) return new(null, problem);

            // A server that omits or zeroes expires_in must not produce a token treated as valid
            // forever. Fall back to a short life so the next call re-mints rather than reusing
            // something whose lifetime we never learned. RFC 6749 does not require the field.
            Volatile.Write(ref cached, new CachedToken(
                    token, credential.ClientId, tokenUrl,
                    DateTimeOffset.UtcNow.AddSeconds(expiresIn > 0 ? expiresIn : 300)));

            return new(token, null);
        }
        finally {
            Gate.Release();
        }
    }

    /// <summary>The token to serve for this credential and endpoint, or null if it cannot be reused.</summary>
    static string? Reusable(CachedToken? entry, MachineCredential credential, string tokenUrl) =>
        entry is not null
        && string.Equals(entry.ClientId, credential.ClientId, StringComparison.Ordinal)
        && string.Equals(entry.TokenUrl, tokenUrl, StringComparison.Ordinal)
        && DateTimeOffset.UtcNow < entry.Expiry - RenewMargin
            ? entry.Token
            : null;
}

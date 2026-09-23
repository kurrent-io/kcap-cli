using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Auth;

public record StoredTokens {
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_at")]
    public required DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("github_username")]
    public required string GitHubUsername { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "GitHubApp";

    [JsonPropertyName("client_id")]
    public string? ClientId { get; init; }

    /// <summary>
    /// Canonical URL of the server that minted this token (see <see cref="ServerIdentity"/>).
    /// Null on files written before this field existed — those stay unenforced and are stamped
    /// the next time a login or an unambiguous refresh rewrites them.
    /// </summary>
    [JsonPropertyName("server_url")]
    public string? ServerUrl { get; init; }

    /// <summary>Expired, or close enough that a request started now would arrive after expiry.</summary>
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt - TimeSpan.FromSeconds(30);
}

/// <summary>
/// Outcome of resolving a token for a specific target server. <see cref="Tokens"/> is non-null
/// only when <see cref="Status"/> is <see cref="AuthStatus.Ok"/> — a token bound to a different
/// server is never handed out. <see cref="IssuedServerUrl"/> carries the (non-secret) diagnostic
/// from the SAME snapshot the decision was made on, so a caller never has to re-read storage to
/// build its message and can't race a concurrent profile/token change into a mismatched one.
/// </summary>
public sealed record TokenResolution(
    StoredTokens? Tokens,
    AuthStatus    Status,
    string?       IssuedServerUrl,
    string        ProfileName);

// Outcome of a proactive-refresh tick (<see cref="TokenStore.RefreshIfExpiringAsync"/>).
// NotDue = no-op (no tokens, the None provider, or the token still comfortably valid);
// Refreshed = a valid token is now persisted (we refreshed, a peer did, or it was already
// fresh under the lock); Failed = a refresh was attempted but the endpoint never answered
// (transport failure) — the refresh credential may still be live, so a later retry can succeed;
// Rejected = WorkOS refused the refresh token (a non-success response, a 400 invalid_grant the
// usual case — the token is consumed or revoked), which only a fresh `kcap login` repairs, so
// re-sending it is pointless; Contended = we couldn't acquire the cross-process lock before its
// deadline (a peer holds it, presumably refreshing) — no endpoint call was made, so it is NOT a
// failure. The daemon loop rate-limits on Refreshed and Failed, backs off hard on Rejected, and
// treats Contended quietly — no warning, no backoff.
public enum ProactiveRefreshOutcome { NotDue, Refreshed, Failed, Contended, Rejected }

public sealed class TokenStore(
        ConfigRoot config, ProfileOverrides env, IHttpClientFactory httpFactory, WorkOSClient workos, TimeProvider time) {
    string LegacyTokenPath { get; } = config.Path("tokens.json");
    string TokenDir        { get; } = config.Path("tokens");

    static void ValidateProfileName(string profile) {
        if (string.IsNullOrWhiteSpace(profile)) {
            throw new ArgumentException("Profile name must not be empty.", nameof(profile));
        }
        if (profile is "." or "..") {
            throw new ArgumentException("Profile name is invalid.", nameof(profile));
        }
        if (profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            profile.Contains(Path.DirectorySeparatorChar) ||
            profile.Contains(Path.AltDirectorySeparatorChar)) {
            throw new ArgumentException("Profile name contains invalid filename characters.", nameof(profile));
        }
    }

    string ProfileTokenPath(string profile) {
        ValidateProfileName(profile);
        return Path.Combine(TokenDir, $"{profile}.json");
    }

    enum TokenFileState { Missing, Unusable, Loaded }

    // Reads a token file, distinguishing a genuinely-absent file (Missing — a pre-upgrade
    // install may still warrant the legacy fallback) from a present-but-unparseable one
    // (Unusable — corrupt/empty/hand-edited → "not authenticated", never resurrect stale
    // legacy creds). A corrupt file degrades to "run kcap login" instead of throwing
    // JsonException out of every command, hook, the daemon, and MCP; the next successful
    // login/refresh overwrites it. FileNotFound/DirectoryNotFound (a logout deleting the
    // file mid-read) is Missing, not a crash — but real IO/permission faults still
    // propagate and must not be masked as unauthenticated.
    static async Task<(TokenFileState State, StoredTokens? Tokens)> ReadTokenFileAsync(string path) {
        string json;
        try {
            json = await File.ReadAllTextAsync(path);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return (TokenFileState.Missing, null);
        }

        try {
            var tokens = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.StoredTokens);
            return tokens is null ? (TokenFileState.Unusable, null) : (TokenFileState.Loaded, tokens);
        } catch (JsonException) {
            return (TokenFileState.Unusable, null);
        }
    }

    // ── Profile-aware overloads ──────────────────────────────────────────────

    public async Task<StoredTokens?> LoadAsync(string profile) {
        // Missing and Unusable both mean "no usable creds for this profile" → null.
        var (_, tokens) = await ReadTokenFileAsync(ProfileTokenPath(profile));
        return tokens;
    }

    /// The file a profile's credential lives in, for messages that name it.
    public string TokenPath(string profile) => ProfileTokenPath(profile);

    public Task SaveAsync(string profile, StoredTokens tokens, CancellationToken ct = default) =>
        SaveGuardedAsync(profile, tokens, guard: null, ct);

    /// Saves under the profile's cross-process lock when <paramref name="guard"/> holds against the
    /// config as it is at that moment; a null guard always writes.
    public async Task<GuardedWriteOutcome> SaveGuardedAsync(
            string profile, StoredTokens tokens, Func<ProfileConfig, bool>? guard, CancellationToken ct = default) {
        using var lockStream = await AcquireProfileLockAsync(profile, ct);
        var outcome = Evaluate(guard);
        if (outcome == GuardedWriteOutcome.Written) await SaveLockedAsync(profile, tokens, ct);
        return outcome;
    }

    // The write itself, for a caller that already holds the profile's lock.
    async Task SaveLockedAsync(string profile, StoredTokens tokens, CancellationToken ct) {
        Directory.CreateDirectory(TokenDir);
        var path     = ProfileTokenPath(profile);
        // Unique per write so concurrent writers never splice each other's bytes; the atomic
        // File.Move then publishes one complete document, last-writer-wins.
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        // Owner-only from the first byte: a chmod after writing would leave a window where the
        // secret is group/world-readable under the process umask.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try {
            await using (var stream = new FileStream(tempPath, options))
            await using (var writer = new StreamWriter(stream)) {
                await writer.WriteAsync(
                    JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens).AsMemory(), ct);
            }
            await ReplaceWithRetryAsync(tempPath, path, time, ct);
        } finally {
            if (File.Exists(tempPath)) {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
            }
        }

        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Only the owner's save retires the legacy file: another profile's save leaving it is what
        // keeps the active profile's only credential alive.
        if (File.Exists(LegacyTokenPath) && IsLegacyOwner(profile)) {
            try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
        }
    }

    // A guard reads config without the migrating loader; a read that fails is not a pass.
    GuardedWriteOutcome Evaluate(Func<ProfileConfig, bool>? guard) {
        if (guard is null) return GuardedWriteOutcome.Written;
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var cfg)) return GuardedWriteOutcome.ConfigUnreadable;
        return guard(cfg) ? GuardedWriteOutcome.Written : GuardedWriteOutcome.GuardRefused;
    }

    // One lock file per profile, exclusive while open. Null once the holder has kept it past LockWait.
    async Task<FileStream?> TryAcquireProfileLockAsync(string profile, CancellationToken ct) {
        ValidateProfileName(profile);
        Directory.CreateDirectory(TokenDir);
        var lockPath = Path.Combine(TokenDir, $"{profile}.lock");
        var deadline = time.GetUtcNow() + LockWait;

        while (true) {
            ct.ThrowIfCancellationRequested();
            try {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                if (time.GetUtcNow() >= deadline) return null;
                await Task.Delay(LockPollGap, time, ct);
            }
        }
    }

    async Task<FileStream> AcquireProfileLockAsync(string profile, CancellationToken ct) =>
        await TryAcquireProfileLockAsync(profile, ct)
        ?? throw new TimeoutException($"The token file for profile '{profile}' is locked by another process.");

    // Atomically publish the completed temp over the target. On POSIX rename() is atomic and
    // tolerant of a concurrent writer holding the target open, so this succeeds first try. On
    // Windows the replace opens the target with exclusive sharing (the default), so when peers
    // (hooks/watcher/daemon/MCP/login share this store) publish the same profile at once the
    // loser hits ACCESS_DENIED / a sharing violation (UnauthorizedAccessException or IOException)
    // — a transient collision, not a real fault. Retry a bounded number of times with a short
    // backoff; a genuine, persistent failure (e.g. the target is a directory) still surfaces by
    // rethrowing after the budget is spent. File.Move(overwrite: true) is atomic on NTFS, so a
    // reader only ever sees the old or the new complete document, never a splice.
    const int ReplaceMaxAttempts   = 10;
    const int ReplaceBackoffBaseMs = 20;

    static async Task ReplaceWithRetryAsync(
            string tempPath, string path, TimeProvider time, CancellationToken ct = default) {
        for (var attempt = 1; ; attempt++) {
            ct.ThrowIfCancellationRequested();
            try {
                File.Move(tempPath, path, overwrite: true);
                return;
            } catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException)
                                         && attempt < ReplaceMaxAttempts) {
                // Jitter the backoff so concurrent writers don't wake in lockstep and re-collide on
                // the shared target every attempt (the Windows sharing-violation thundering herd).
                await Task.Delay(
                    TimeSpan.FromMilliseconds(ReplaceBackoffBaseMs * attempt + Random.Shared.Next(ReplaceBackoffBaseMs)),
                    time, ct);
            }
        }
    }

    /// Deletes the profile's credential under its lock when <paramref name="guard"/> holds against
    /// the config as it is at that moment; a null guard always deletes. A lock that cannot be taken
    /// or a delete that fails throws, so a caller can report the file it could not remove.
    public async Task<GuardedWriteOutcome> DeleteGuardedAsync(
            string profile, Func<ProfileConfig, bool>? guard, CancellationToken ct = default) {
        using var lockStream = await AcquireProfileLockAsync(profile, ct);
        var outcome = Evaluate(guard);
        if (outcome == GuardedWriteOutcome.Written) DeleteLocked(profile);
        return outcome;
    }

    void DeleteLocked(string profile) {
        var path = ProfileTokenPath(profile);
        if (File.Exists(path)) File.Delete(path);
        SweepLeakedTemps(profile);
    }

    // Best-effort removal of the profile's temp files leaked by a crash between write and move
    // ({profile}.json.{pid}.{guid}.tmp) — these carry token secrets. Only under the profile's
    // lock: a live writer's temp is unlinked otherwise, and its publish fails. Matched by
    // filename prefix (not a glob) so a profile name containing a wildcard char can't widen it.
    void SweepLeakedTemps(string profile) {
        if (!Directory.Exists(TokenDir)) return;
        var prefix = $"{profile}.json.";
        try {
            foreach (var tmp in Directory.EnumerateFiles(TokenDir, "*.tmp")) {
                if (Path.GetFileName(tmp).StartsWith(prefix, StringComparison.Ordinal)) {
                    try { File.Delete(tmp); } catch { /* best-effort */ }
                }
            }
        } catch { /* best-effort */ }
    }

    // The profile a leaked temp belongs to, or null for a file that is not one of ours.
    static string? TempOwner(string fileName) {
        if (!fileName.EndsWith(".tmp", StringComparison.Ordinal)) return null;
        var at = fileName.LastIndexOf(".json.", StringComparison.Ordinal);
        return at > 0 ? fileName[..at] : null;
    }

    // ── Legacy (profile-resolving) overloads ────────────────────────────────

    // Read a profile's token file, falling back to the legacy single-file tokens.json ONLY when the
    // per-profile file is genuinely absent (a pre-upgrade install). A present-but-corrupt active
    // file is "not authenticated" — do NOT resurrect stale credentials from a legacy file whose
    // best-effort deletion previously failed. Shared by LoadAsync() and RefreshIfExpiringAsync so
    // both get the legacy fallback without re-resolving the active profile.
    //
    // The legacy file is a single global credential belonging to whichever profile was active
    // before the per-profile store existed — its migration owner. Now that lookup follows the
    // RESOLVED profile, an unscoped fallback would hand that credential to any repo-resolved
    // profile that simply has no token yet (and it carries no ServerUrl, so the binding check
    // can't catch it either). Restrict it to the migration owner.
    async Task<StoredTokens?> LoadWithLegacyFallbackAsync(string profile, CancellationToken ct = default) {
        var (state, tokens) = await ReadTokenFileAsync(ProfileTokenPath(profile));

        if (state == TokenFileState.Loaded) return tokens;

        if (state == TokenFileState.Missing && IsLegacyOwner(profile)) {
            var (_, legacy) = await ReadTokenFileAsync(LegacyTokenPath);
            return legacy;
        }

        return null; // Unusable (corrupt), or a profile that doesn't own the legacy credential
    }

    // The legacy credential's owner is the on-disk active profile, with an absent/empty value
    // normalizing to "default". Deliberately NOT "profile == active || profile == default": with
    // active profile Y, a resolution landing on "default" must not pick up Y's legacy credential.
    // A pure read: the migrating loader could take the config lock under the token lock. An
    // unreadable config names no owner — it must not authorize deleting another profile's credential.
    bool IsLegacyOwner(string profile) =>
        ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var cfg)
        && string.Equals(profile, cfg.ActiveName, StringComparison.Ordinal);

    /// Logout. Each credential goes under its own lock, so a refresh holding one finishes and its
    /// result is deleted rather than recreated after the fact. The active profile's file and the
    /// legacy file go under one hold of the active lock: a legacy-only refresh slipping between two
    /// holds would recreate the profile file after its delete.
    public async Task DeleteAsync(CancellationToken ct = default) {
        ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var cfg);
        var names = new HashSet<string>(cfg.Profiles.Keys, StringComparer.Ordinal);
        if (Directory.Exists(TokenDir)) {
            try {
                // A leaked temp names its owner too, so it is swept under that owner's lock.
                foreach (var file in Directory.EnumerateFiles(TokenDir)) {
                    var fileName = Path.GetFileName(file);
                    if (fileName.EndsWith(".json", StringComparison.Ordinal)) names.Add(Path.GetFileNameWithoutExtension(fileName));
                    else if (TempOwner(fileName) is { } owner) names.Add(owner);
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort: config's names still get deleted */ }
        }
        names.Remove(cfg.ActiveName);

        foreach (var name in names) {
            try { await DeleteGuardedAsync(name, guard: null, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort, per file */ }
        }

        try {
            using var lockStream = await AcquireProfileLockAsync(cfg.ActiveName, ct);
            DeleteLocked(cfg.ActiveName);
            if (File.Exists(LegacyTokenPath)) File.Delete(LegacyTokenPath);
        } catch (ArgumentException) {
            // A name the layout rejects can hold no lock and own no file, so nothing races this delete.
            try { if (File.Exists(LegacyTokenPath)) File.Delete(LegacyTokenPath); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }
        } catch (Exception ex) when (ex is not OperationCanceledException) { /* best-effort */ }
    }

    /// Whether <paramref name="other"/> reads the file <paramref name="profile"/>'s credential is
    /// in: the same name, or a case-alias where the filesystem folds case. Two spellings the
    /// directory lists separately are two files, whatever <see cref="File.Exists(string)"/> says.
    public bool SharesTokenFile(string profile, string other) {
        if (string.Equals(profile, other, StringComparison.Ordinal)) return true;
        if (!string.Equals(profile, other, StringComparison.OrdinalIgnoreCase)) return false;

        var path      = ProfileTokenPath(profile);
        var otherPath = ProfileTokenPath(other);
        if (!File.Exists(path) || !File.Exists(otherPath)) return false;

        var listed = Directory.EnumerateFiles(TokenDir).Select(f => Path.GetFileName(f)).ToHashSet(StringComparer.Ordinal);
        return !(listed.Contains(Path.GetFileName(path)) && listed.Contains(Path.GetFileName(otherPath)));
    }

    /// Settles the legacy <c>tokens.json</c> under <paramref name="owner"/>'s lock: moved into the
    /// owner's own file when that is absent, deleted when the owner already has one (valid or
    /// corrupt, the legacy copy is superseded either way), untouched when there is none. Throws on
    /// a failed move or delete, so a caller about to change the active profile can refuse instead
    /// of leaving the file for the next profile to claim.
    public async Task MigrateLegacyAsync(string owner, CancellationToken ct = default) {
        if (!File.Exists(LegacyTokenPath)) return;
        using var lockStream = await AcquireProfileLockAsync(owner, ct);
        if (!File.Exists(LegacyTokenPath)) return;

        var target = ProfileTokenPath(owner);
        if (File.Exists(target)) {
            File.Delete(LegacyTokenPath);
            return;
        }

        File.Move(LegacyTokenPath, target);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Resolves a token for a specific target server. This is the ONLY way a bearer token should
    /// reach an outgoing request: a token bound to a different server is withheld here, before any
    /// network call, instead of being sent and rejected with an opaque 401 the CLI cannot explain.
    ///
    /// The binding check deliberately runs on the raw snapshot BEFORE any refresh — refreshing a
    /// token we are about to refuse to use would spend a rotating credential (and a round-trip to
    /// its own server) for nothing.
    /// </summary>
    public async Task<TokenResolution> GetValidTokensForServerAsync(
            string profile, string targetBaseUrl, CancellationToken ct = default) {
        var snapshot = await LoadWithLegacyFallbackAsync(profile, ct);

        if (snapshot is null) {
            return new(null, AuthStatus.NotAuthenticated, null, profile);
        }

        if (!BoundToTarget(snapshot, targetBaseUrl)) {
            return new(null, AuthStatus.WrongServer, snapshot.ServerUrl, profile);
        }

        // Pinned to the profile resolved above: re-resolving here would let a concurrent
        // `kcap use` switch profiles mid-call and return another profile's token while
        // TokenResolution.ProfileName still names the first one.
        var valid = await GetValidTokensForProfileAsync(profile, ct);

        if (valid is null) {
            return new(null, AuthStatus.Expired, snapshot.ServerUrl, profile);
        }

        // Re-check: the load/refresh above re-reads storage, so a concurrent login or profile
        // repoint could have replaced the token we vetted with one for a different server.
        // Checking only the first snapshot would let that replacement through.
        return BoundToTarget(valid, targetBaseUrl)
            ? new(valid, AuthStatus.Ok, valid.ServerUrl, profile)
            : new(null, AuthStatus.WrongServer, valid.ServerUrl, profile);
    }

    /// <summary>
    /// Whether <paramref name="tokens"/> may be presented to <paramref name="targetBaseUrl"/>.
    /// An unbound (pre-upgrade) token is permitted — there is nothing to contradict.
    /// </summary>
    static bool BoundToTarget(StoredTokens tokens, string targetBaseUrl) =>
        tokens.ServerUrl is null || ServerIdentity.SameServer(tokens.ServerUrl, targetBaseUrl);

    /// <summary>
    /// Recovers a usable token after the server rejected <paramref name="rejectedAccessToken"/>.
    ///
    /// Rotation is attempted first. If it fails, the fallback is a RAW read — deliberately not the
    /// refresh-aware accessor, which would see the same expired token and refresh a second time,
    /// re-spending a WorkOS refresh token that is single-use.
    ///
    /// The raw result is returned even when it equals the rejected token. Resending it is usually
    /// futile, but it is the recovery that shipped (a server that rejected transiently — a rolling
    /// restart, a node with stale key material — accepts the very next attempt), and the callers
    /// retry at most once either way.
    /// </summary>
    public async Task<StoredTokens?> RecoverForServerAsync(
            string profile, string targetBaseUrl, string rejectedAccessToken, CancellationToken ct = default) {
        var rotated = await ForceRefreshForProfileAsync(profile, rejectedAccessToken, targetBaseUrl, ct);

        if (rotated is not null) return rotated;

        var stored = await LoadWithLegacyFallbackAsync(profile, ct);

        if (stored is null) return null;

        return BoundToTarget(stored, targetBaseUrl) ? stored : null;
    }

    /// <summary>
    /// Raw, refresh-free read for an explicitly named profile (honouring the owner-scoped legacy
    /// fallback). For diagnostics that must observe exactly what is on disk without mutating it.
    /// </summary>
    public Task<StoredTokens?> LoadForProfileAsync(string profile, CancellationToken ct = default) =>
        LoadWithLegacyFallbackAsync(profile, ct);

    // Load-and-refresh for ONE profile, named by the caller. Everything that refreshes goes
    // through here, so a single call can't straddle a concurrent profile switch.
    public async Task<StoredTokens?> GetValidTokensForProfileAsync(string profile, CancellationToken ct = default) {
        var tokens = await LoadWithLegacyFallbackAsync(profile, ct);

        if (tokens is null) {
            return null;
        }

        if (!tokens.IsExpiredAt(time.GetUtcNow())) {
            return tokens;
        }

        // Both providers rotate/re-issue on refresh, so serialize across processes
        // (hooks, watcher, daemon, MCP share one token store) with a profile-scoped
        // file lock — otherwise a peer refreshing with the same rotated-out WorkOS
        // refresh token would invalidate the session.
        if (tokens is { Provider: "workos", RefreshToken: not null, ClientId: not null }) {
            return await RefreshWithCrossProcessLockAsync(profile, tokens, RefreshWorkOSAsync, cancellationToken: ct);
        }

        // GitHub: refresh via server's /auth/refresh endpoint
        if (tokens.Provider is "GitHubApp") {
            return await RefreshWithCrossProcessLockAsync(
                profile, tokens, t => RefreshGitHubAsync(profile, t, ct), cancellationToken: ct);
        }

        return null;
    }

    /// <summary>
    /// Forces one provider refresh even when the locally cached access token has not expired.
    /// Used only after a server 401 proves that the otherwise-valid token is no longer accepted.
    /// The existing profile-scoped lock still serializes rotating credentials across processes.
    ///
    /// <paramref name="rejectedAccessToken"/> is the token the failing request actually sent.
    /// Refreshing is conditional on the persisted token still BEING that one: if a peer process
    /// rotated in between, its fresh token is adopted as-is. Refreshing unconditionally would
    /// rotate a credential that was never rejected — and for WorkOS, whose refresh token is
    /// single-use, that is a real cost, not just extra traffic.
    /// </summary>
    /// <param name="expectedServerUrl">
    /// The server the caller is about to retry against. The lock may hand back a token a PEER
    /// process persisted rather than one we rotated, and that peer could have logged into a
    /// different server — so the adopted token is binding-checked before it is returned. Callers
    /// that have no target (there are none today) may pass null to skip the check.
    /// </param>
    public Task<StoredTokens?> ForceRefreshAsync(
            string profile, string rejectedAccessToken, string? expectedServerUrl = null, CancellationToken ct = default) =>
        ForceRefreshForProfileAsync(profile, rejectedAccessToken, expectedServerUrl, ct);

    // Forced rotation for ONE profile, resolved by the caller — so a recovery that rotates and
    // then falls back to a raw read cannot straddle a concurrent `kcap use` and return a
    // different profile's token than the one it just tried to rotate.
    async Task<StoredTokens?> ForceRefreshForProfileAsync(
            string profile, string rejectedAccessToken, string? expectedServerUrl, CancellationToken ct) {
        var tokens = await LoadWithLegacyFallbackAsync(profile, ct);
        if (tokens is null) return null;

        Func<StoredTokens, Task<StoredTokens?>> refresh = tokens.Provider switch {
            AuthProvider.WorkOS when tokens.RefreshToken is not null && tokens.ClientId is not null
                => value => RefreshWorkOSAsync(value, ct),
            AuthProvider.GitHubApp => value => RefreshGitHubAsync(profile, value, ct),
            _ => _ => Task.FromResult<StoredTokens?>(null)
        };
        if (tokens.Provider is not (AuthProvider.WorkOS or AuthProvider.GitHubApp)) return null;

        var refreshed = await RefreshWithCrossProcessLockAsync(
            profile,
            tokens,
            refresh,
            needsRefresh: t => string.Equals(t.AccessToken, rejectedAccessToken, StringComparison.Ordinal),
            cancellationToken: ct);

        if (refreshed is null || expectedServerUrl is null) return refreshed;

        return BoundToTarget(refreshed, expectedServerUrl) ? refreshed : null;
    }

    // The decision the daemon's proactive-refresh tick makes each time it wakes. Kept a
    // pure function (no IO) so every branch is unit-testable in isolation;
    // RefreshIfExpiringAsync turns the decision into action.
    internal enum RefreshDecision { NoTokens, NotDueYet, RefreshWorkOS, RefreshGitHub, Unsupported }

    // Should the active profile's token be refreshed ahead of expiry, and via which provider
    // path? Refresh only once the token is within `window` of `ExpiresAt` — outside it we leave
    // the refresh credential untouched so proactive refresh adds no measurable traffic. Provider
    // gating mirrors GetValidTokensForProfileAsync: WorkOS needs its rotating refresh_token + client_id;
    // GitHub re-mints via the server. Anything else — the None provider, or a WorkOS token
    // missing its credentials — is a no-op.
    internal static RefreshDecision DecideProactiveRefresh(StoredTokens? tokens, DateTimeOffset now, TimeSpan window) {
        if (tokens is null) {
            return RefreshDecision.NoTokens;
        }

        if (now < tokens.ExpiresAt - window) {
            return RefreshDecision.NotDueYet;
        }

        if (tokens is { Provider: AuthProvider.WorkOS, RefreshToken: not null, ClientId: not null }) {
            return RefreshDecision.RefreshWorkOS;
        }

        if (tokens.Provider is AuthProvider.GitHubApp) {
            return RefreshDecision.RefreshGitHub;
        }

        return RefreshDecision.Unsupported;
    }

    // Proactively refresh the active profile's token when it is within `window` of expiry —
    // even though it isn't expired yet. The daemon calls this on a low-frequency timer so a
    // continuously-running daemon keeps a WorkOS sliding-inactivity session alive (up to its
    // absolute lifetime) instead of the user hitting a 401 on the next hook after an idle
    // period and being forced to re-run `kcap login`.
    //
    // Goes through the same profile-scoped cross-process lock as GetValidTokensForProfileAsync, so it
    // never races a hook/watcher/MCP refresh and clobbers a rotated WorkOS refresh token, and
    // refreshes only inside the window. The daemon loop rate-limits attempts, so refresh
    // traffic stays bounded even when a token keeps landing back inside the window.
    //
    // Returns a ProactiveRefreshOutcome (see that enum). The refresh calls swallow network /
    // parse failures (return null → Failed); a genuine IO fault reading the token or config
    // can still propagate and is caught by the daemon's total tick.
    public async Task<ProactiveRefreshOutcome> RefreshIfExpiringAsync(string profile, TimeSpan window) {
        // The profile is threaded through the read and the lock; the lock helper also persists under
        // it, so a profile switch mid-call can't make us refresh — or write — one profile's token
        // under another profile's lock. Use the legacy-fallback loader so a pre-upgrade install
        // (only tokens.json, no per-profile file) is still refreshed (and migrated into the
        // per-profile store when the refresh persists).
        var tokens = await LoadWithLegacyFallbackAsync(profile);

        var decision = DecideProactiveRefresh(tokens, time.GetUtcNow(), window);

        if (decision is not (RefreshDecision.RefreshWorkOS or RefreshDecision.RefreshGitHub)) {
            return ProactiveRefreshOutcome.NotDue;
        }

        // Re-evaluate the window under the lock too (via this predicate): a peer may refresh
        // between our read here and our acquiring the lock, leaving the re-read token fresh.
        bool ExpiringWithinWindow(StoredTokens t) => time.GetUtcNow() >= t.ExpiresAt - window;

        // Capture the raw WorkOS classification the same way `contended` captures lock contention:
        // the delegate records it, the code after the lock reads it. Only set when the WorkOS
        // refresh delegate actually runs (a peer-refresh or still-fresh re-read returns non-null
        // without calling it), so a null result plus a Rejected outcome is a genuine refusal.
        WorkOSRefreshOutcome? workosOutcome = null;

        var refresh = decision == RefreshDecision.RefreshWorkOS
            ? (Func<StoredTokens, Task<StoredTokens?>>)(t => RefreshWorkOSAsync(t, CancellationToken.None, o => workosOutcome = o))
            : t => RefreshGitHubAsync(profile, t, CancellationToken.None);

        var contended = false;
        var result    = await RefreshWithCrossProcessLockAsync(
            profile, tokens!, refresh, ExpiringWithinWindow, onLockContended: () => contended = true);

        // Lock contention (a peer holds the lock, likely mid-refresh) is not a refresh failure —
        // don't let the daemon warn/back off as though the endpoint rejected us. Otherwise:
        // non-null = a valid token is now persisted; null = the refresh call actually failed.
        if (contended) return ProactiveRefreshOutcome.Contended;
        if (result is not null) return ProactiveRefreshOutcome.Refreshed;

        // A refused WorkOS refresh token is terminal — re-sending it is pointless, so the daemon
        // backs off hard on Rejected rather than the ordinary Failed retry cadence.
        return workosOutcome is WorkOSRefreshOutcome.Rejected
            ? ProactiveRefreshOutcome.Rejected
            : ProactiveRefreshOutcome.Failed;
    }

    // How long a waiter holds out for the lock: the holder's whole WorkOS replay budget plus room to
    // persist. A shorter wait gives up on a holder that is one replay away from saving a fresh token
    // and falls back to the stale one instead.
    static readonly TimeSpan LockWaitMargin = TimeSpan.FromSeconds(10);
    static readonly TimeSpan LockPollGap    = TimeSpan.FromMilliseconds(100);

    internal TimeSpan LockWait => workos.RefreshBudget + LockWaitMargin;

    // Profile-scoped cross-process lock. Acquire it, re-read the token under it (a peer
    // may have just rotated it), refresh only if it is still due per `needsRefresh`, persist,
    // release. If the lock can't be acquired within the deadline, fall back to whatever a peer
    // persisted; `onLockContended` (proactive path only) is invoked when we give up still-due so
    // the caller can distinguish lock contention from an actual refresh failure.
    internal async Task<StoredTokens?> RefreshWithCrossProcessLockAsync(
            string                                  profile,
            StoredTokens                            current,
            Func<StoredTokens, Task<StoredTokens?>> refresh,
            Func<StoredTokens, bool>?               needsRefresh    = null,
            Action?                                 onLockContended = null,
            CancellationToken                       cancellationToken = default
        ) {
        // Default predicate: refresh a token GetValidTokensForProfileAsync already found expired. The
        // proactive path (RefreshIfExpiringAsync) passes a wider "within N minutes of expiry"
        // predicate so it can refresh ahead of expiry — under this same lock, re-checked after
        // the re-read, so it can't race a peer's refresh or double-spend a rotated token.
        needsRefresh ??= t => t.IsExpiredAt(time.GetUtcNow());

        var lockStream = await TryAcquireProfileLockAsync(profile, cancellationToken);

        if (lockStream is null) {
            var latest = await LoadAsync(profile);

            // A peer refreshed while we waited → return their fresh token.
            if (latest is not null && !needsRefresh(latest)) {
                return latest;
            }

            // Gave up still-due: a peer holds the lock (likely mid-refresh). Signal contention so
            // the proactive caller doesn't report this as a refresh failure.
            onLockContended?.Invoke();

            return null;
        }

        try {
            // Re-read under the lock. A file deleted since the pre-lock read is a sign-out, not a
            // credential to bring back from the copy in hand; a file the pre-lock read parsed but
            // is corrupt now is refreshed from that copy.
            var (state, onDisk) = await ReadTokenFileAsync(ProfileTokenPath(profile));
            StoredTokens latest;
            if (state == TokenFileState.Loaded) {
                latest = onDisk!;
            } else if (state == TokenFileState.Missing) {
                var legacy = IsLegacyOwner(profile) ? (await ReadTokenFileAsync(LegacyTokenPath)).Tokens : null;
                if (legacy is null) return null;
                latest = legacy;
            } else {
                latest = current;
            }

            // A peer refreshed while we waited for the lock (the persisted token changed) and its
            // result is still valid → don't refresh again, even if the fresh token is still inside
            // the proactive window. A short-lived / JwtExpiry-fallback token would otherwise be
            // double-rotated (and double the endpoint traffic) right after a peer just rotated it.
            // The reactive path is unaffected: its needsRefresh is IsExpired, already false here.
            if (latest.AccessToken != current.AccessToken && !latest.IsExpiredAt(time.GetUtcNow())) {
                return latest;
            }

            if (!needsRefresh(latest)) {
                return latest;
            }

            var refreshed = await refresh(latest);

            // Persist under THIS profile's lock, with the profile we locked. The refresh delegates
            // deliberately do NOT persist themselves: resolving the active profile at write time
            // would let a switch mid-refresh write this profile's rotated token into another
            // profile's file, without that profile's lock.
            if (refreshed is not null) {
                await SaveLockedAsync(profile, refreshed, cancellationToken);
            }

            return refreshed;
        } finally {
            // Close the stream to release the OS lock, but DON'T delete the file: on Unix a
            // waiter can acquire the old inode between dispose and delete, then the unlink lets
            // another process create a fresh lock file — splitting the lock and allowing two
            // concurrent refreshes. The (tiny, one-per-profile) lock file stays in place.
            lockStream.Dispose();
        }
    }

    // WorkOS access tokens are JWTs carrying their own `exp`. Read it without signature
    // validation (the server validates against JWKS); fall back to a short lifetime.
    public static DateTimeOffset JwtExpiry(string accessToken, TimeProvider time) {
        try {
            var parts = accessToken.Split('.');

            if (parts.Length >= 2) {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

                using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));

                if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)) {
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
                }
            }
        } catch {
            // Malformed token — fall through to the conservative default.
        }

        return time.GetUtcNow().AddMinutes(5);
    }

    // Short retry budget for the refresh HTTP call. A bare single-shot POST turned any
    // transient blip (DNS stutter, connection reset, brief server slowness) into a hard
    // "token expired — run kcap login", even though the refresh credential was still valid.
    // PostWithRetryAsync retries only transport failures, never non-success responses — so a
    // genuinely-expired refresh token still returns fast (400/401 → null, no pointless retries).
    // Kept short so a hook never blocks for the default 30s budget when the server is down.
    static readonly TimeSpan RefreshRetryBudget = TimeSpan.FromSeconds(5);

    async Task<StoredTokens?> RefreshGitHubAsync(string profile, StoredTokens tokens, CancellationToken ct) {
        // A bound token refreshes against the server that minted it: /auth/refresh validates the
        // existing token's signature, so posting it anywhere else can only fail — and would leak
        // it to a server that never issued it. An unbound (pre-upgrade) token names no minter, so
        // it falls back to KCAP_URL and then to the profile's own server_url.
        var configured = tokens.ServerUrl is not null
            ? null
            : env.Url
              ?? (await AppConfig.LoadProfileConfig(config, ct)).Profiles.GetValueOrDefault(profile)?.ServerUrl;

        var baseUrl = tokens.ServerUrl ?? configured ?? "http://localhost:5108";
        var url     = $"{baseUrl}/auth/refresh";

        // Stamp an unbound (pre-upgrade) token only when the endpoint came from real configuration.
        // The localhost default is a fallback, not evidence of where the token was minted.
        var stamped = tokens.ServerUrl ?? (configured is not null ? ServerIdentity.Canonicalize(configured) : null);

        // Reached from daemon/background callers via GetValidTokensForProfileAsync, which expects a
        // stale-token failure here, not an exception — so an unusable URL degrades to null rather
        // than the UnusableServerUrlException an interactive command path would throw.
        if (!HttpClientExtensions.IsAcceptableUrl(url)) {
            return null;
        }

        // The anonymous lane: this posts to our own server, but the token travels in the body, so
        // there is no bearer to rotate and a recovery handler here would recurse into this refresh.
        using var http = httpFactory.CreateClient(CapacitorClients.Anonymous);

        var requestBody = JsonSerializer.Serialize(
            new() { AccessToken = tokens.AccessToken },
            CapacitorJsonContext.Default.RefreshTokenRequest
        );
        var payload = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

        try {
            var response = await http.PostWithRetryAsync(url, payload, time, RefreshRetryBudget, ct);

            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync(
                CapacitorJsonContext.Default.TokenExchangeResponse, ct);

            if (json is null) {
                return null;
            }

            // Persistence is the caller's responsibility (RefreshWithCrossProcessLockAsync saves
            // under the locked profile). Returning without saving keeps a rotated token from being
            // written to the wrong profile if the active profile changes mid-refresh.
            return tokens with {
                AccessToken = json.AccessToken,
                ExpiresAt = time.GetUtcNow().AddSeconds(json.ExpiresIn),
                ServerUrl = stamped
            };
        } catch {
            return null;
        }
    }

    Task<StoredTokens?> RefreshWorkOSAsync(StoredTokens tokens) =>
        RefreshWorkOSAsync(tokens, CancellationToken.None);

    // `onOutcome` reports the raw WorkOS classification so a caller (the proactive tick) can tell a
    // refused refresh token — terminal, `kcap login` repairs it — from a transport failure it may
    // retry. A non-Rotated outcome maps to null; only the proactive tick reads onOutcome.
    async Task<StoredTokens?> RefreshWorkOSAsync(
            StoredTokens tokens, CancellationToken ct, Action<WorkOSRefreshOutcome>? onOutcome = null) {
        var result = await workos.RefreshAsync(tokens.ClientId!, tokens.RefreshToken!, ct);

        onOutcome?.Invoke(result.Outcome);

        if (result.Outcome is not WorkOSRefreshOutcome.Rotated) {
            return null;
        }

        var json = result.Response!;

        // Persistence is the caller's responsibility (RefreshWithCrossProcessLockAsync saves under the
        // locked profile) — see RefreshGitHubAsync. WorkOS rotates the refresh token on use, so writing
        // under the wrong profile would be especially damaging.
        return tokens with {
            AccessToken  = json.AccessToken,
            ExpiresAt    = JwtExpiry(json.AccessToken, time),
            RefreshToken = json.RefreshToken ?? tokens.RefreshToken
        };
    }
}

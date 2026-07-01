using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Config;

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

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromSeconds(30);
}

public static class TokenStore {
    static string LegacyTokenPath => PathHelpers.ConfigPath("tokens.json");
    static string TokenDir         => PathHelpers.ConfigPath("tokens");

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

    static string ProfileTokenPath(string profile) {
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

    public static async Task<StoredTokens?> LoadAsync(string profile) {
        // Missing and Unusable both mean "no usable creds for this profile" → null.
        var (_, tokens) = await ReadTokenFileAsync(ProfileTokenPath(profile));
        return tokens;
    }

    public static async Task SaveAsync(string profile, StoredTokens tokens) {
        Directory.CreateDirectory(TokenDir);
        var path     = ProfileTokenPath(profile);
        // Unique per write so concurrent writers (hooks/watcher/daemon/MCP/login share this
        // store) never write the same temp file and splice each other's bytes. The atomic
        // File.Move then publishes one complete document, last-writer-wins.
        var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        // Create the temp owner-only from the very first byte. A chmod *after* writing would
        // leave a window where the token secret exists group/world-readable under the process
        // umask (and a crash in that window leaks a readable temp). UnixCreateMode applies the
        // mode at creation; it is ignored on Windows, so set it only on Unix. The rename then
        // carries 0600 onto the final file.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try {
            await using (var stream = new FileStream(tempPath, options))
            await using (var writer = new StreamWriter(stream)) {
                await writer.WriteAsync(JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens));
            }
            await ReplaceWithRetryAsync(tempPath, path);
        } finally {
            // Success renames the temp away; only a failed write/move leaves it. Unlike the
            // old shared name, a leaked unique temp never gets reused, so clean it up.
            if (File.Exists(tempPath)) {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
            }
        }

        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Migration: remove the pre-upgrade single-file token if it still exists
        if (File.Exists(LegacyTokenPath)) {
            try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
        }
    }

    // Atomically publish the completed temp over the target. On POSIX rename() is atomic and
    // tolerant of a concurrent writer holding the target open, so this succeeds first try. On
    // Windows the replace opens the target with exclusive sharing (the default), so when peers
    // (hooks/watcher/daemon/MCP/login share this store) publish the same profile at once the
    // loser hits ACCESS_DENIED / a sharing violation (UnauthorizedAccessException or IOException)
    // — a transient collision, not a real fault. Retry a bounded number of times with a short
    // backoff; a genuine, persistent failure (e.g. the target is a directory) still surfaces by
    // rethrowing after the budget is spent. File.Move(overwrite: true) is atomic on NTFS, so a
    // reader only ever sees the old or the new complete document, never a splice.
    const int ReplaceMaxAttempts   = 5;
    const int ReplaceBackoffBaseMs = 20;

    static async Task ReplaceWithRetryAsync(string tempPath, string path) {
        for (var attempt = 1; ; attempt++) {
            try {
                File.Move(tempPath, path, overwrite: true);
                return;
            } catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException)
                                         && attempt < ReplaceMaxAttempts) {
                // Linear backoff (20/40/60/80ms) to let the peer holding the target close it.
                await Task.Delay(ReplaceBackoffBaseMs * attempt);
            }
        }
    }

    public static void Delete(string profile) {
        var path = ProfileTokenPath(profile);
        if (File.Exists(path)) File.Delete(path);
        SweepLeakedTemps(profile);
    }

    // Best-effort removal of temp files leaked by a crash between write and move
    // ({profile}.json.{pid}.{guid}.tmp) — these carry token secrets. Pass a profile to
    // scope to that profile's temps, or null to sweep all (logout). Matched by filename
    // prefix (not a glob) so a profile name containing a wildcard char can't widen it.
    static void SweepLeakedTemps(string? profile = null) {
        if (!Directory.Exists(TokenDir)) return;
        var prefix = profile is null ? null : $"{profile}.json.";
        try {
            foreach (var tmp in Directory.EnumerateFiles(TokenDir, "*.tmp")) {
                if (prefix is null || Path.GetFileName(tmp).StartsWith(prefix, StringComparison.Ordinal)) {
                    try { File.Delete(tmp); } catch { /* best-effort */ }
                }
            }
        } catch { /* best-effort */ }
    }

    // ── Legacy (profile-resolving) overloads ────────────────────────────────

    public static async Task<StoredTokens?> LoadAsync() {
        var profile           = await ResolveActiveProfileAsync();
        var (state, tokens)   = await ReadTokenFileAsync(ProfileTokenPath(profile));

        if (state == TokenFileState.Loaded) return tokens;

        // Only a genuinely-absent per-profile file means "pre-upgrade install" and
        // warrants the legacy single-file fallback. A present-but-corrupt active file is
        // "not authenticated" — do NOT resurrect stale credentials from a legacy file
        // whose best-effort deletion previously failed.
        if (state == TokenFileState.Missing) {
            var (_, legacy) = await ReadTokenFileAsync(LegacyTokenPath);
            return legacy;
        }

        return null; // Unusable (corrupt) active profile
    }

    public static async Task SaveAsync(StoredTokens tokens) {
        var profile = await ResolveActiveProfileAsync();
        await SaveAsync(profile, tokens);
    }

    public static Task DeleteAsync() {
        if (File.Exists(LegacyTokenPath)) {
            try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
        }

        if (Directory.Exists(TokenDir)) {
            try {
                foreach (var file in Directory.EnumerateFiles(TokenDir, "*.json")) {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            } catch { /* best-effort */ }
        }

        // Also remove any leaked temps (they carry token secrets) so logout leaves nothing behind.
        SweepLeakedTemps();

        return Task.CompletedTask;
    }

    static async Task<string> ResolveActiveProfileAsync() {
        var cfg = await AppConfig.LoadProfileConfig();
        return string.IsNullOrEmpty(cfg.ActiveProfile) ? "default" : cfg.ActiveProfile;
    }

    public static async Task<StoredTokens?> GetValidTokensAsync() {
        var tokens = await LoadAsync();

        if (tokens is null) {
            return null;
        }

        if (!tokens.IsExpired) {
            return tokens;
        }

        var profile = await ResolveActiveProfileAsync();

        // Both providers rotate/re-issue on refresh, so serialize across processes
        // (hooks, watcher, daemon, MCP share one token store) with a profile-scoped
        // file lock — otherwise a peer refreshing with the same rotated-out WorkOS
        // refresh token would invalidate the session.
        if (tokens is { Provider: "workos", RefreshToken: not null, ClientId: not null }) {
            return await RefreshWithCrossProcessLockAsync(profile, tokens, RefreshWorkOSAsync);
        }

        // GitHub: refresh via server's /auth/refresh endpoint
        if (tokens.Provider is "GitHubApp") {
            return await RefreshWithCrossProcessLockAsync(profile, tokens, RefreshGitHubAsync);
        }

        return null;
    }

    // Profile-scoped cross-process lock. Acquire it, re-read the token under it (a peer
    // may have just rotated it), refresh only if still expired, persist, release. If the
    // lock can't be acquired within the deadline, fall back to whatever a peer persisted.
    static async Task<StoredTokens?> RefreshWithCrossProcessLockAsync(
            string                                  profile,
            StoredTokens                            current,
            Func<StoredTokens, Task<StoredTokens?>> refresh
        ) {
        // Validate before building the lock path — a profile name with path separators
        // must not let the lock file escape TokenDir (matches ProfileTokenPath's guard).
        ValidateProfileName(profile);
        Directory.CreateDirectory(TokenDir);
        var lockPath = Path.Combine(TokenDir, $"{profile}.lock");

        FileStream? lockStream = null;
        var         deadline   = DateTime.UtcNow.AddSeconds(15);

        while (lockStream is null) {
            try {
                lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                if (DateTime.UtcNow >= deadline) {
                    var latest = await LoadAsync(profile);

                    return latest is { IsExpired: false } ? latest : null;
                }

                await Task.Delay(100);
            }
        }

        try {
            var latest = await LoadAsync(profile) ?? current;

            return latest.IsExpired ? await refresh(latest) : latest;
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
    public static DateTimeOffset JwtExpiry(string accessToken) {
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

        return DateTimeOffset.UtcNow.AddMinutes(5);
    }

    // Short retry budget for the refresh HTTP call. A bare single-shot POST turned any
    // transient blip (DNS stutter, connection reset, brief server slowness) into a hard
    // "token expired — run kcap login", even though the refresh credential was still valid.
    // PostWithRetryAsync retries only transport failures, never non-success responses — so a
    // genuinely-expired refresh token still returns fast (400/401 → null, no pointless retries).
    // Kept short so a hook never blocks for the default 30s budget when the server is down.
    static readonly TimeSpan RefreshRetryBudget = TimeSpan.FromSeconds(5);

    static async Task<StoredTokens?> RefreshGitHubAsync(StoredTokens tokens) {
        var baseUrl = AppConfig.ResolvedServerUrl ?? Environment.GetEnvironmentVariable("KCAP_URL") ?? "http://localhost:5108";
        var url     = $"{baseUrl}/auth/refresh";

        // PostWithRetryAsync runs EnsureAbsolute, which Environment.Exit(2)s on a scheme-less URL.
        // Refresh is reached from daemon/background callers via GetValidTokensAsync and must fail
        // gracefully (return null), never terminate the process — so validate here instead of
        // letting the retry helper exit. The hook *entry* paths still EnsureAbsolute-and-exit by
        // design; this guard only covers the refresh call.
        if (!HttpClientExtensions.IsAcceptableUrl(url)) {
            return null;
        }

        using var http = new HttpClient();

        var requestBody = JsonSerializer.Serialize(
            new() { AccessToken = tokens.AccessToken },
            CapacitorJsonContext.Default.RefreshTokenRequest
        );
        var payload = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");

        try {
            var response = await http.PostWithRetryAsync(url, payload, RefreshRetryBudget);

            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.TokenExchangeResponse);

            if (json is null) {
                return null;
            }

            var refreshed = tokens with {
                AccessToken = json.AccessToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json.ExpiresIn)
            };

            await SaveAsync(refreshed);

            return refreshed;
        } catch {
            return null;
        }
    }

    static async Task<StoredTokens?> RefreshWorkOSAsync(StoredTokens tokens) {
        // Mirror RefreshGitHubAsync: network/parse failures return null (caller surfaces
        // "run kcap login") rather than throwing out of GetValidTokensAsync and crashing.
        try {
            using var http = new HttpClient();

            // Retries only transport failures. WorkOS rotates the refresh token on each
            // successful use, so a retry after the server already rotated (response lost in
            // transit) would re-send the now-consumed token. That reuse window already exists
            // without retry — the next refresh call re-reads the same unrotated token from disk
            // and re-sends it — so the short retry doesn't add a new failure mode; it just rides
            // out the common case where the request never reached WorkOS.
            using var response = await http.PostWithRetryAsync(
                "https://api.workos.com/user_management/authenticate",
                new FormUrlEncodedContent(
                    new Dictionary<string, string> {
                        ["grant_type"]    = "refresh_token",
                        ["client_id"]     = tokens.ClientId!,
                        ["refresh_token"] = tokens.RefreshToken!
                    }
                ),
                RefreshRetryBudget
            );

            if (!response.IsSuccessStatusCode) {
                return null;
            }

            var json = await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.WorkOSAuthResponse);

            if (json is null) {
                return null;
            }

            var refreshed = tokens with {
                AccessToken = json.AccessToken,
                ExpiresAt = JwtExpiry(json.AccessToken),
                RefreshToken = json.RefreshToken ?? tokens.RefreshToken
            };

            await SaveAsync(refreshed);

            return refreshed;
        } catch {
            return null;
        }
    }
}

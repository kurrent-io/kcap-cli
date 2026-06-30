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

    // A corrupt, empty, partially-written, or hand-edited token file is equivalent to
    // "no usable credentials": return null so the CLI degrades to "run kcap login"
    // instead of throwing JsonException out of every command, hook, the daemon, and MCP.
    // The next successful login/refresh overwrites the file. Catch JsonException only —
    // IO/permission errors are real faults that must not be masked as unauthenticated.
    static async Task<StoredTokens?> ReadTokensAsync(string path) {
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        try {
            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.StoredTokens);
        } catch (JsonException) {
            return null;
        }
    }

    // ── Profile-aware overloads ──────────────────────────────────────────────

    public static async Task<StoredTokens?> LoadAsync(string profile) {
        return await ReadTokensAsync(ProfileTokenPath(profile));
    }

    public static async Task SaveAsync(string profile, StoredTokens tokens) {
        Directory.CreateDirectory(TokenDir);
        var path     = ProfileTokenPath(profile);
        var tempPath = $"{path}.tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(tokens, CapacitorJsonContext.Default.StoredTokens));
        File.Move(tempPath, path, overwrite: true);

        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // Migration: remove the pre-upgrade single-file token if it still exists
        if (File.Exists(LegacyTokenPath)) {
            try { File.Delete(LegacyTokenPath); } catch { /* best-effort */ }
        }
    }

    public static void Delete(string profile) {
        var path = ProfileTokenPath(profile);
        if (File.Exists(path)) File.Delete(path);
    }

    // ── Legacy (profile-resolving) overloads ────────────────────────────────

    public static async Task<StoredTokens?> LoadAsync() {
        var profile    = await ResolveActiveProfileAsync();
        var perProfile = await LoadAsync(profile);
        if (perProfile is not null) return perProfile;

        // Fall back to legacy single-file layout for pre-upgrade installs
        return await ReadTokensAsync(LegacyTokenPath);
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

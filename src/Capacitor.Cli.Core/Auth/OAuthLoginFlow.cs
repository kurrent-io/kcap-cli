using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// ReSharper disable MethodHasAsyncOverload

namespace Capacitor.Cli.Core.Auth;

public static class AuthProvider {
    public const string GitHubApp = "GitHubApp";
    public const string WorkOS    = "workos";
    public const string None      = "None";
}

public enum GitHubFlow { Browser, Device }

public static class OAuthLoginFlow {
    public static async Task<int> LoginWithDiscoveryAsync(string serverUrl, bool forceDevice) {
        // ReSharper disable once ShortLivedHttpClient
        using var http = new HttpClient();

        HttpResponseMessage configResponse;

        try {
            configResponse = await http.GetAsync($"{serverUrl}/auth/config");
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(serverUrl, ex);

            return 1;
        }

        if (!configResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error: Failed to fetch auth config from {serverUrl}/auth/config");

            return 1;
        }

        var config = (await configResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AuthDiscoveryResponse))!;

        return config.Provider switch {
            AuthProvider.None      => HandleNoneLogin(),
            AuthProvider.GitHubApp => await HandleGitHubLogin(serverUrl, config, forceDevice),
            AuthProvider.WorkOS    => await HandleWorkOSLogin(config),
            _                      => HandleUnknownProvider(config.Provider)
        };
    }

    internal static GitHubFlow ChooseGitHubFlow(bool forceDevice, bool isHeadless, bool hasExchangeUrl)
        => forceDevice || isHeadless || !hasExchangeUrl ? GitHubFlow.Device : GitHubFlow.Browser;

    static int HandleNoneLogin() {
        Console.Out.WriteLine("Server has no authentication configured — login not required.");

        return 0;
    }

    static int HandleUnknownProvider(string provider) {
        Console.Error.WriteLine($"Error: Unknown auth provider '{provider}'. Update your kcap CLI.");

        return 1;
    }

    /// <summary>
    /// Runs GitHub Device Flow interactively. Prints the user code and verification URL to
    /// <see cref="Console.Out"/>, opens the system browser to the verification URL, and polls
    /// GitHub for the access token. Intended for CLI use — not suitable for headless callers.
    /// </summary>
    /// <returns>The GitHub access token on success, or <c>null</c> on failure.</returns>
    public static async Task<string?> RunDeviceFlowAsync(string clientId) {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new("application/json"));

        var deviceResponse = await http.PostAsync(
            "https://github.com/login/device/code",
            new FormUrlEncodedContent(
                new Dictionary<string, string> {
                    ["client_id"] = clientId,
                    ["scope"]     = "read:user read:org"
                }
            )
        );

        if (!deviceResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error requesting device code: {await deviceResponse.Content.ReadAsStringAsync()}");

            return null;
        }

        var device   = (await deviceResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.GitHubDeviceCodeResponse))!;
        var interval = device.Interval;

        var copied = Clipboard.TryCopy(device.UserCode);

        bool browserOpened;

        try {
            Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
            browserOpened = true;
        } catch {
            // Browser open is best-effort — headless environments (devcontainers, SSH) have none.
            browserOpened = false;
        }

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync("To finish signing in to GitHub:");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(
            browserOpened
                ? $"  1. Your browser should have opened {device.VerificationUri}"
                : $"  1. Open {device.VerificationUri} in a browser"
        );

        if (browserOpened) await Console.Out.WriteLineAsync("     (if it didn't open, go to that URL yourself)");

        await Console.Out.WriteLineAsync($"  2. Enter the code: {device.UserCode}{(copied ? "  (copied to clipboard)" : "")}");
        await Console.Out.WriteLineAsync("  3. Approve access when GitHub asks.");
        await Console.Out.WriteLineAsync();

        Console.Write("Waiting for you to authorize...");

        while (true) {
            await Task.Delay(TimeSpan.FromSeconds(interval));

            var tokenResponse = await http.PostAsync(
                "https://github.com/login/oauth/access_token",
                new FormUrlEncodedContent(
                    new Dictionary<string, string> {
                        ["client_id"]   = clientId,
                        ["device_code"] = device.DeviceCode,
                        ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code"
                    }
                )
            );

            var tokenResult = (await tokenResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.GitHubTokenResponse))!;

            if (tokenResult.AccessToken is not null) {
                await Console.Out.WriteLineAsync(" done!");

                return tokenResult.AccessToken;
            }

            switch (tokenResult.Error) {
                case "authorization_pending":
                    Console.Write(".");

                    continue;
                case "slow_down":
                    interval += 5;

                    continue;
                default:
                    Console.Error.WriteLine($"\nError: {tokenResult.Error}");

                    return null;
            }
        }
    }

    /// <summary>
    /// Runs GitHub authorization-code-with-PKCE flow against a localhost loopback
    /// listener. Opens the system browser to GitHub's authorize page; on callback,
    /// verifies CSRF state and POSTs the code+verifier to <paramref name="codeExchangeUrl"/>
    /// on the Capacitor server. The server adds its GitHub App client_secret and forwards
    /// to GitHub's token endpoint (which GitHub Apps require, even with PKCE).
    /// Returns the token on success, or <c>null</c> on user cancel, state mismatch,
    /// or upstream error. Throws if the loopback port can't be bound — the caller
    /// uses that signal to fall back to device flow.
    /// </summary>
    public static async Task<string?> RunGitHubBrowserFlowAsync(string clientId, string codeExchangeUrl, TimeSpan? timeout = null) {
        var verifier  = GenerateCodeVerifier();
        var challenge = GenerateCodeChallenge(verifier);
        var state     = GenerateCodeVerifier(); // reuse the random source — same entropy is fine

        var port        = GetAvailablePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var authUrl = BuildGitHubAuthorizeUrl(clientId, redirectUri, state, challenge);

        await Console.Out.WriteLineAsync("Opening browser for GitHub authentication...");
        await Console.Out.WriteLineAsync($"  If the browser doesn't open, visit: {authUrl}");

        try {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        } catch {
            /* Browser open is best-effort — user can still copy the URL */
        }

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));

        HttpListenerContext context;

        while (true) {
            var getContext = listener.GetContextAsync();

            try {
                context = await getContext.WaitAsync(cts.Token);
            } catch (OperationCanceledException) {
                listener.Stop();
                _ = getContext.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Console.Error.WriteLine("Timed out waiting for authorization. Re-run `kcap login` to try again.");

                return null;
            }

            if (context.Request.Url?.AbsolutePath == "/callback") break;

            // Ignore favicon and other browser-issued requests that aren't our callback.
            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        var callback = ParseCallback(context.Request.Url?.Query ?? "", state);
        await RespondCallbackAsync(context, callback);
        listener.Stop();

        if (callback.Code is null) {
            Console.Error.WriteLine($"Authorization failed: {callback.Error}");

            return null;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Accept.Add(new("application/json"));

        var exchangeRequest = new GitHubCodeExchangeRequest {
            Code         = callback.Code,
            CodeVerifier = verifier,
            RedirectUri  = redirectUri
        };

        HttpResponseMessage tokenResponse;

        try {
            tokenResponse = await http.PostAsJsonAsync(
                codeExchangeUrl,
                exchangeRequest,
                CapacitorJsonContext.Default.GitHubCodeExchangeRequest,
                cancellationToken: cts.Token
            );
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException) {
            Console.Error.WriteLine($"Could not reach the code-exchange endpoint at {codeExchangeUrl}: {ex.Message}");

            return null;
        }

        if (!tokenResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error exchanging code: {await tokenResponse.Content.ReadAsStringAsync()}");

            return null;
        }

        GitHubTokenResponse? tokenResult;

        try {
            tokenResult = await tokenResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.GitHubTokenResponse, cancellationToken: cts.Token);
        } catch (JsonException ex) {
            var raw = await tokenResponse.Content.ReadAsStringAsync(cts.Token);
            Console.Error.WriteLine($"Code-exchange response was not valid JSON ({ex.Message}): {raw}");

            return null;
        }

        if (tokenResult?.AccessToken is null) {
            Console.Error.WriteLine($"Error: {tokenResult?.Error ?? "no access_token in response"}");

            return null;
        }

        await Console.Out.WriteLineAsync("Authorization complete.");

        return tokenResult.AccessToken;
    }

    static async Task RespondCallbackAsync(HttpListenerContext ctx, CallbackResult callback) {
        var (status, message) = callback.Code is not null
            ? ("Authentication successful!", "You can close this window and return to the terminal.")
            : ($"Authentication failed: {callback.Error}", "Return to the terminal for details.");

        var html = $"<html><body style='font-family:system-ui;max-width:480px;margin:80px auto;text-align:center'>"
          + $"<h2>{WebUtility.HtmlEncode(status)}</h2>"
          + $"<p>{WebUtility.HtmlEncode(message)}</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType     = "text/html";
        ctx.Response.ContentLength64 = buffer.Length;
        await ctx.Response.OutputStream.WriteAsync(buffer);
        ctx.Response.Close();
    }

    public static async Task<int> ExchangeAndSaveAsync(string serverUrl, string githubAccessToken, string provider) {
        if (provider is not AuthProvider.GitHubApp) {
            Console.Error.WriteLine($"Error: unknown auth provider '{provider}'");

            return 1;
        }

        using var http = new HttpClient();

        var exchangeResponse = await http.PostAsJsonAsync(
            $"{serverUrl}/auth/token",
            new() { GithubAccessToken = githubAccessToken },
            CapacitorJsonContext.Default.TokenExchangeRequest
        );

        if (!exchangeResponse.IsSuccessStatusCode) {
            WriteExchangeError(await exchangeResponse.Content.ReadAsStringAsync(), profile: null);

            return 1;
        }

        var exchange = (await exchangeResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.TokenExchangeResponse))!;

        await TokenStore.SaveAsync(
            new() {
                AccessToken    = exchange.AccessToken,
                ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(exchange.ExpiresIn),
                GitHubUsername = exchange.Username,
                Provider       = provider
            }
        );

        await Console.Out.WriteLineAsync($"Logged in as {exchange.Username}");

        return 0;
    }

    /// <summary>
    /// Exchanges a GitHub access token for a Capacitor JWT and saves it to the named profile.
    /// Unlike the single-argument overload, this does NOT print "Logged in as …" — the caller
    /// is responsible for user-facing output. Returns 0 on success, 1 on failure.
    /// </summary>
    public static async Task<int> ExchangeAndSaveAsync(string serverUrl, string githubAccessToken, string provider, string profile) {
        using var http = new HttpClient();

        return await ExchangeAndSaveAsync(http, serverUrl, githubAccessToken, provider, profile);
    }

    public static async Task<int> ExchangeAndSaveAsync(
            HttpClient http,
            string     serverUrl,
            string     githubAccessToken,
            string     provider,
            string     profile
        ) {
        if (provider is not AuthProvider.GitHubApp) {
            Console.Error.WriteLine($"Error: unknown auth provider '{provider}'");

            return 1;
        }

        var exchangeResponse = await http.PostAsJsonAsync(
            $"{serverUrl}/auth/token",
            new TokenExchangeRequest { GithubAccessToken = githubAccessToken },
            CapacitorJsonContext.Default.TokenExchangeRequest
        );

        if (!exchangeResponse.IsSuccessStatusCode) {
            WriteExchangeError(await exchangeResponse.Content.ReadAsStringAsync(), profile);

            return 1;
        }

        var exchange = (await exchangeResponse.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.TokenExchangeResponse))!;

        await TokenStore.SaveAsync(
            profile,
            new StoredTokens {
                AccessToken    = exchange.AccessToken,
                ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(exchange.ExpiresIn),
                GitHubUsername = exchange.Username,
                Provider       = provider
            }
        );

        return 0;
    }

    /// <summary>
    /// Prints the server's <c>/auth/token</c> error to stderr. When the server reports that
    /// the Capacitor GitHub App isn't installed on the user's org, appends a troubleshooting
    /// checklist — the most common cause is the device-flow consent being completed under a
    /// different GitHub account than the one with org membership.
    /// </summary>
    static void WriteExchangeError(string body, string? profile) {
        var prefix = profile is null
            ? "Error exchanging token"
            : $"Error exchanging token for profile '{profile}'";

        var serverMessage = TryParseInstallationMessage(body);

        if (serverMessage is null) {
            Console.Error.WriteLine($"{prefix}: {body}");

            return;
        }

        Console.Error.WriteLine($"{prefix}: {serverMessage}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("This usually means the Capacitor GitHub App isn't visible to your GitHub user.");
        Console.Error.WriteLine("Common fixes:");
        Console.Error.WriteLine("  1. Authorize as the right GitHub account. The device-flow page authorizes");
        Console.Error.WriteLine("     whoever is signed in to your browser — sign in to https://github.com as");
        Console.Error.WriteLine("     your org user, then re-run `kcap setup ...`.");
        Console.Error.WriteLine("  2. If your org enforces SAML SSO, authorize SSO for the App at");
        Console.Error.WriteLine("     https://github.com/settings/apps/authorizations.");
        Console.Error.WriteLine("  3. Revoke a stale prior authorization at the same URL and retry.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("If the App was never installed on your org, an org admin must install it.");
    }

    internal static string? TryParseInstallationMessage(string body) {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try {
            var parsed = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.AuthErrorResponse);
            var msg    = parsed?.Error;

            return msg is not null && msg.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                ? msg
                : null;
        } catch (JsonException) {
            return null;
        }
    }

    static async Task<int> HandleGitHubLogin(string serverUrl, AuthDiscoveryResponse config, bool forceDevice) {
        var accessToken = await AcquireGitHubTokenAsync(config.GithubClientId!, config.GithubCodeExchangeUrl, forceDevice);

        if (accessToken is null) return 1;

        return await ExchangeAndSaveAsync(serverUrl, accessToken, config.Provider);
    }

    internal static async Task<string?> AcquireGitHubTokenAsync(string clientId, string? codeExchangeUrl, bool forceDevice) {
        var headless = HeadlessEnvironment.IsHeadless();
        var choice   = ChooseGitHubFlow(forceDevice, headless, hasExchangeUrl: IsValidExchangeUrl(codeExchangeUrl));

        if (choice == GitHubFlow.Browser) {
            try {
                var token = await RunGitHubBrowserFlowAsync(clientId, codeExchangeUrl!);

                return token ??
                    // Browser flow ran but user cancelled / state mismatch — don't silently fall back.
                    null;
            } catch (HttpListenerException ex) {
                Console.Error.WriteLine($"Could not bind loopback listener ({ex.Message}); falling back to device flow.");
            } catch (PlatformNotSupportedException ex) {
                Console.Error.WriteLine($"Loopback listener not supported on this platform ({ex.Message}); falling back to device flow.");
            }
        }

        return await RunDeviceFlowAsync(clientId);
    }

    static Task<int> HandleWorkOSLogin(AuthDiscoveryResponse config) =>
        LoginWorkOSAsync(config.AuthKitDomain, config.ClientId!, config.OrganizationId);

    const string WorkOSApiBase = "https://api.workos.com";

    /// <summary>
    /// WorkOS AuthKit authorization-code-with-PKCE login on a 127.0.0.1 loopback listener
    /// (WorkOS documents the HTTP loopback exception as 127.0.0.1, not localhost). Authorize
    /// on the AuthKit domain (hosted UI; falls back to api.workos.com), org-scoped when known;
    /// the token exchange always hits api.workos.com. Public client — no client secret.
    /// </summary>
    static async Task<int> LoginWorkOSAsync(string? authKitDomain, string clientId, string? organizationId) {
        var authorizeBase = string.IsNullOrEmpty(authKitDomain) ? WorkOSApiBase : $"https://{authKitDomain}";

        var loop = await RunWorkOSLoopbackAsync(authorizeBase, clientId, organizationId);
        if (loop.Code is null) {
            Console.Error.WriteLine(loop.Error switch {
                "state_mismatch" => "Error: state mismatch — possible CSRF. Aborting.",
                "timeout"        => "Timed out waiting for authorization. Re-run `kcap login` to try again.",
                _                => "Error: No authorization code received."
            });

            return 1;
        }

        using var http = new HttpClient();

        var json = await AuthenticateWorkOSCodeAsync(http, WorkOSApiBase, clientId, loop.Code, loop.Verifier);
        if (json is null) {
            Console.Error.WriteLine("Error: WorkOS token exchange failed.");

            return 1;
        }

        // Org gate: a multi-org user must not be "logged in" to the wrong org — every API
        // call would then fail the server's org check. Reject before saving tokens.
        if (!string.IsNullOrEmpty(organizationId) && !string.Equals(json.OrganizationId, organizationId, StringComparison.Ordinal)) {
            Console.Error.WriteLine($"Error: signed in to the wrong WorkOS organization (expected {organizationId}). Re-run `kcap login` and pick the correct organization.");

            return 1;
        }

        var username = WorkOSDisplayName(json.User);

        await TokenStore.SaveAsync(
            new() {
                AccessToken    = json.AccessToken,
                RefreshToken   = json.RefreshToken,
                ExpiresAt      = TokenStore.JwtExpiry(json.AccessToken),
                GitHubUsername = username,
                Provider       = AuthProvider.WorkOS,
                ClientId       = clientId
            }
        );

        await Console.Out.WriteLineAsync($"Logged in as {username}");

        return 0;
    }

    /// <summary>Human display name from a WorkOS user (first+last, else email, else "unknown").</summary>
    internal static string WorkOSDisplayName(WorkOSUserInfo? user) {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(user?.FirstName)) parts.Add(user!.FirstName!);
        if (!string.IsNullOrEmpty(user?.LastName))  parts.Add(user!.LastName!);

        return parts.Count > 0 ? string.Join(' ', parts) : user?.Email ?? "unknown";
    }

    public sealed record WorkOSLoopbackResult(string? Code, string Verifier, string RedirectUri, string? Error);

    /// <summary>
    /// WorkOS AuthKit authorization-code-with-PKCE on a 127.0.0.1 loopback listener. Authorizes on
    /// <paramref name="authorizeBase"/>, org-scoped only when <paramref name="organizationId"/> is set
    /// (pass null for org-less discovery). Returns the authorization code + PKCE verifier, or an
    /// <c>Error</c> ("timeout" / "state_mismatch" / "missing_code"). Public client — the separate token
    /// exchange carries no secret.
    /// </summary>
    public static async Task<WorkOSLoopbackResult> RunWorkOSLoopbackAsync(
            string authorizeBase, string clientId, string? organizationId, TimeSpan? timeout = null) {
        var verifier    = GenerateCodeVerifier();
        var challenge   = GenerateCodeChallenge(verifier);
        var state       = GenerateCodeVerifier();
        var port        = GetAvailablePort();
        var redirectUri = $"http://127.0.0.1:{port}/callback";

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var authUrl = $"{authorizeBase}/user_management/authorize?"          +
            $"response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"             +
            $"&provider=authkit"                                             +
            (string.IsNullOrEmpty(organizationId) ? "" : $"&organization_id={Uri.EscapeDataString(organizationId)}") +
            $"&state={Uri.EscapeDataString(state)}"                          +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        await Console.Out.WriteLineAsync("Opening browser for authentication...");
        await Console.Out.WriteLineAsync($"  If the browser doesn't open, visit: {authUrl}");

        try {
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
        } catch {
            /* Browser open is best-effort — user can still copy the URL */
        }

        // Bounded wait + ignore non-callback requests (favicon etc.) — mirrors RunGitHubBrowserFlowAsync.
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));

        HttpListenerContext context;

        while (true) {
            var getContext = listener.GetContextAsync();

            try {
                context = await getContext.WaitAsync(cts.Token);
            } catch (OperationCanceledException) {
                listener.Stop();
                _ = getContext.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                return new(null, verifier, redirectUri, "timeout");
            }

            if (context.Request.Url?.AbsolutePath == "/callback") break;

            // Ignore favicon and other browser-issued requests that aren't our callback.
            context.Response.StatusCode = 404;
            context.Response.Close();
        }

        var code          = context.Request.QueryString["code"];
        var returnedState = context.Request.QueryString["state"];

        if (returnedState != state) {
            const string errHtml = "<html><body><h2>Authentication failed</h2><p>State mismatch — possible CSRF. Return to the terminal.</p></body></html>";
            var          errBuf  = Encoding.UTF8.GetBytes(errHtml);
            context.Response.ContentType     = "text/html";
            context.Response.ContentLength64 = errBuf.Length;
            await context.Response.OutputStream.WriteAsync(errBuf);
            context.Response.Close();
            listener.Stop();

            return new(null, verifier, redirectUri, "state_mismatch");
        }

        const string html = "<html><body><h2>Authentication successful!</h2><p>You can close this window.</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType     = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
        listener.Stop();

        return string.IsNullOrEmpty(code)
            ? new(null, verifier, redirectUri, "missing_code")
            : new(code, verifier, redirectUri, null);
    }

    /// <summary>Public-client WorkOS code→token exchange at <c>{apiBase}/user_management/authenticate</c>.</summary>
    public static async Task<WorkOSAuthResponse?> AuthenticateWorkOSCodeAsync(
            HttpClient http, string apiBase, string clientId, string code, string codeVerifier) {
        var resp = await http.PostAsync(
            $"{apiBase.TrimEnd('/')}/user_management/authenticate",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"]    = "authorization_code",
                ["client_id"]     = clientId,
                ["code"]          = code,
                ["code_verifier"] = codeVerifier
            }));

        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.WorkOSAuthResponse);
    }

    /// <summary>
    /// Public-client WorkOS org-switch: exchanges a refresh token for an org-scoped token. The spike
    /// confirmed the resulting refresh token stays bound to the org, so subsequent refreshes need no
    /// organization_id. No client secret.
    /// </summary>
    public static async Task<WorkOSAuthResponse?> SwitchWorkOSOrgAsync(
            HttpClient http, string apiBase, string clientId, string refreshToken, string organizationId) {
        var resp = await http.PostAsync(
            $"{apiBase.TrimEnd('/')}/user_management/authenticate",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["grant_type"]      = "refresh_token",
                ["client_id"]       = clientId,
                ["refresh_token"]   = refreshToken,
                ["organization_id"] = organizationId
            }));

        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.WorkOSAuthResponse);
    }

    internal readonly record struct CallbackResult(string? Code, string? Error);

    // The server-supplied code-exchange URL must be a fully-qualified http(s) URI before
    // we trust it. An empty string, whitespace, relative path, or javascript:/file: URL
    // is treated as "no browser flow available" and the dispatcher falls back to device flow.
    internal static bool IsValidExchangeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed)
     && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

    internal static string BuildGitHubAuthorizeUrl(
            string clientId,
            string redirectUri,
            string state,
            string codeChallenge
        ) =>
        "https://github.com/login/oauth/authorize?"              +
        $"client_id={Uri.EscapeDataString(clientId)}"            +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"     +
        $"&state={Uri.EscapeDataString(state)}"                  +
        $"&scope={Uri.EscapeDataString("read:user read:org")}"   +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
        "&code_challenge_method=S256"                            +
        "&response_type=code";

    internal static CallbackResult ParseCallback(string queryString, string expectedState) {
        var     qs    = queryString.TrimStart('?');
        var     parts = qs.Split('&', StringSplitOptions.RemoveEmptyEntries);
        string? code  = null, state = null, error = null;

        foreach (var part in parts) {
            var eq = part.IndexOf('=');

            if (eq < 0) continue;

            var key = part[..eq];
            var val = Uri.UnescapeDataString(part[(eq + 1)..]);

            switch (key) {
                case "code":  code  = val; break;
                case "state": state = val; break;
                case "error": error = val; break;
            }
        }

        if (state is null) return new(null, "missing_state");
        if (state != expectedState) return new(null, "state_mismatch");
        if (error is not null) return new(null, error);

        return string.IsNullOrEmpty(code) ? new(null, "missing_code") : new(code, null);
    }

    static string GenerateCodeVerifier() {
        var bytes = RandomNumberGenerator.GetBytes(32);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static string GenerateCodeChallenge(string verifier) {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));

        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static int GetAvailablePort() {
        var tcpListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcpListener.Start();
        var port = ((IPEndPoint)tcpListener.LocalEndpoint).Port;
        tcpListener.Stop();

        return port;
    }
}

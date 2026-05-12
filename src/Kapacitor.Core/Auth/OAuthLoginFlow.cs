using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
// ReSharper disable MethodHasAsyncOverload

namespace kapacitor.Auth;

public static class AuthProvider {
    public const string GitHubApp = "GitHubApp";
    public const string Auth0     = "Auth0";
    public const string None      = "None";
}

public enum GitHubFlow { Browser, Device }

public static class OAuthLoginFlow {
    public static Task<int> LoginWithDiscoveryAsync(string serverUrl)
        => LoginWithDiscoveryAsync(serverUrl, forceDevice: false);

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

        var config = (await configResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.AuthDiscoveryResponse))!;

        return config.Provider switch {
            AuthProvider.None      => HandleNoneLogin(),
            AuthProvider.GitHubApp => await HandleGitHubLogin(serverUrl, config, forceDevice),
            AuthProvider.Auth0     => await HandleAuth0Login(config),
            _                      => HandleUnknownProvider(config.Provider)
        };
    }

    internal static GitHubFlow ChooseGitHubFlow(bool forceDevice, bool isHeadless)
        => forceDevice || isHeadless ? GitHubFlow.Device : GitHubFlow.Browser;

    static int HandleNoneLogin() {
        Console.Out.WriteLine("Server has no authentication configured — login not required.");

        return 0;
    }

    static int HandleUnknownProvider(string provider) {
        Console.Error.WriteLine($"Error: Unknown auth provider '{provider}'. Update your kapacitor CLI.");

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
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["client_id"] = clientId,
                ["scope"]     = "read:user read:org"
            })
        );

        if (!deviceResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error requesting device code: {await deviceResponse.Content.ReadAsStringAsync()}");

            return null;
        }

        var device   = (await deviceResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.GitHubDeviceCodeResponse))!;
        var interval = device.Interval;

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"  Enter code: {device.UserCode}");
        await Console.Out.WriteLineAsync($"  at: {device.VerificationUri}");
        await Console.Out.WriteLineAsync();

        try {
            Process.Start(new ProcessStartInfo(device.VerificationUri) { UseShellExecute = true });
        } catch {
            /* Browser open is best-effort */
        }

        Console.Write("Waiting for authorization...");

        while (true) {
            await Task.Delay(TimeSpan.FromSeconds(interval));

            var tokenResponse = await http.PostAsync(
                "https://github.com/login/oauth/access_token",
                new FormUrlEncodedContent(new Dictionary<string, string> {
                    ["client_id"]   = clientId,
                    ["device_code"] = device.DeviceCode,
                    ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code"
                })
            );

            var tokenResult = (await tokenResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.GitHubTokenResponse))!;

            if (tokenResult.AccessToken is not null) {
                await Console.Out.WriteLineAsync(" done!");

                return tokenResult.AccessToken;
            }

            switch (tokenResult.Error) {
                case "authorization_pending":
                    Console.Write("."); continue;
                case "slow_down":
                    interval += 5; continue;
                default:
                    Console.Error.WriteLine($"\nError: {tokenResult.Error}");

                    return null;
            }
        }
    }

    /// <summary>
    /// Runs GitHub authorization-code-with-PKCE flow against a localhost loopback
    /// listener. Opens the system browser to GitHub's authorize page; on callback,
    /// verifies CSRF state and exchanges code+verifier for an access token.
    /// Returns the token on success, or <c>null</c> on user cancel, state mismatch,
    /// or upstream error. Throws if the loopback port can't be bound — the caller
    /// uses that signal to fall back to device flow.
    /// </summary>
    public static async Task<string?> RunGitHubBrowserFlowAsync(string clientId, TimeSpan? timeout = null) {
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
            Task<HttpListenerContext> getContext = listener.GetContextAsync();
            try {
                context = await getContext.WaitAsync(cts.Token);
            } catch (OperationCanceledException) {
                listener.Stop();
                _ = getContext.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                Console.Error.WriteLine("Timed out waiting for authorization. Re-run `kapacitor login` to try again.");
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

        var tokenResponse = await http.PostAsync(
            "https://github.com/login/oauth/access_token",
            new FormUrlEncodedContent(new Dictionary<string, string> {
                ["client_id"]     = clientId,
                ["code"]          = callback.Code,
                ["redirect_uri"]  = redirectUri,
                ["code_verifier"] = verifier,
                ["grant_type"]    = "authorization_code"
            }));

        if (!tokenResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error exchanging code: {await tokenResponse.Content.ReadAsStringAsync()}");
            return null;
        }

        var tokenResult = (await tokenResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.GitHubTokenResponse))!;
        if (tokenResult.AccessToken is null) {
            Console.Error.WriteLine($"Error: {tokenResult.Error ?? "no access_token in response"}");
            return null;
        }

        await Console.Out.WriteLineAsync("Authorization complete.");
        return tokenResult.AccessToken;
    }

    static async Task RespondCallbackAsync(HttpListenerContext ctx, CallbackResult callback) {
        var (status, message) = callback.Code is not null
            ? ("Authentication successful!",  "You can close this window and return to the terminal.")
            : ($"Authentication failed: {callback.Error}", "Return to the terminal for details.");

        var html = $"<html><body style='font-family:system-ui;max-width:480px;margin:80px auto;text-align:center'>"
                 + $"<h2>{System.Net.WebUtility.HtmlEncode(status)}</h2>"
                 + $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType     = "text/html";
        ctx.Response.ContentLength64 = buffer.Length;
        await ctx.Response.OutputStream.WriteAsync(buffer);
        ctx.Response.Close();
    }

    public static async Task<int> ExchangeAndSaveAsync(string serverUrl, string githubAccessToken, string provider) {
        if (provider is not AuthProvider.GitHubApp and not AuthProvider.Auth0) {
            Console.Error.WriteLine($"Error: unknown auth provider '{provider}'");

            return 1;
        }

        using var http = new HttpClient();

        var exchangeResponse = await http.PostAsJsonAsync(
            $"{serverUrl}/auth/token",
            new TokenExchangeRequest { GithubAccessToken = githubAccessToken },
            KapacitorJsonContext.Default.TokenExchangeRequest
        );

        if (!exchangeResponse.IsSuccessStatusCode) {
            WriteExchangeError(await exchangeResponse.Content.ReadAsStringAsync(), profile: null);

            return 1;
        }

        var exchange = (await exchangeResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.TokenExchangeResponse))!;

        await TokenStore.SaveAsync(new StoredTokens {
            AccessToken    = exchange.AccessToken,
            ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(exchange.ExpiresIn),
            GitHubUsername = exchange.Username,
            Provider       = provider
        });

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
            HttpClient http, string serverUrl, string githubAccessToken, string provider, string profile) {
        if (provider is not AuthProvider.GitHubApp and not AuthProvider.Auth0) {
            Console.Error.WriteLine($"Error: unknown auth provider '{provider}'");

            return 1;
        }

        var exchangeResponse = await http.PostAsJsonAsync(
            $"{serverUrl}/auth/token",
            new TokenExchangeRequest { GithubAccessToken = githubAccessToken },
            KapacitorJsonContext.Default.TokenExchangeRequest
        );

        if (!exchangeResponse.IsSuccessStatusCode) {
            WriteExchangeError(await exchangeResponse.Content.ReadAsStringAsync(), profile);

            return 1;
        }

        var exchange = (await exchangeResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.TokenExchangeResponse))!;

        await TokenStore.SaveAsync(profile, new StoredTokens {
            AccessToken    = exchange.AccessToken,
            ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(exchange.ExpiresIn),
            GitHubUsername = exchange.Username,
            Provider       = provider
        });

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
        Console.Error.WriteLine("     your org user, then re-run `kapacitor setup ...`.");
        Console.Error.WriteLine("  2. If your org enforces SAML SSO, authorize SSO for the App at");
        Console.Error.WriteLine("     https://github.com/settings/apps/authorizations.");
        Console.Error.WriteLine("  3. Revoke a stale prior authorization at the same URL and retry.");
        Console.Error.WriteLine();
        Console.Error.WriteLine("If the App was never installed on your org, an org admin must install it.");
    }

    internal static string? TryParseInstallationMessage(string body) {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try {
            var parsed = JsonSerializer.Deserialize(body, KapacitorJsonContext.Default.AuthErrorResponse);
            var msg    = parsed?.Error;

            return msg is not null && msg.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                ? msg
                : null;
        } catch (JsonException) {
            return null;
        }
    }

    static async Task<int> HandleGitHubLogin(string serverUrl, AuthDiscoveryResponse config, bool forceDevice) {
        var accessToken = await AcquireGitHubTokenAsync(config.GithubClientId!, forceDevice);
        if (accessToken is null) return 1;

        return await ExchangeAndSaveAsync(serverUrl, accessToken, config.Provider);
    }

    internal static async Task<string?> AcquireGitHubTokenAsync(string clientId, bool forceDevice) {
        var headless = HeadlessEnvironment.IsHeadless();
        var choice   = ChooseGitHubFlow(forceDevice, headless);

        if (choice == GitHubFlow.Browser) {
            try {
                var token = await RunGitHubBrowserFlowAsync(clientId);
                if (token is not null) return token;
                // Browser flow ran but user cancelled / state mismatch — don't silently fall back.
                return null;
            } catch (HttpListenerException ex) {
                Console.Error.WriteLine($"Could not bind loopback listener ({ex.Message}); falling back to device flow.");
            } catch (PlatformNotSupportedException ex) {
                Console.Error.WriteLine($"Loopback listener not supported on this platform ({ex.Message}); falling back to device flow.");
            }
        }

        return await RunDeviceFlowAsync(clientId);
    }

    static async Task<int> HandleAuth0Login(AuthDiscoveryResponse config) {
        return await LoginAsync(config.Auth0Domain!, config.ClientId!, config.Audience ?? "");
    }

    /// <summary>
    /// Auth0 PKCE login flow (preserved for Auth0 strategy).
    /// </summary>
    static async Task<int> LoginAsync(string auth0Domain, string clientId, string audience) {
        var verifier    = GenerateCodeVerifier();
        var challenge   = GenerateCodeChallenge(verifier);
        var port        = GetAvailablePort();
        var redirectUri = $"http://localhost:{port}/callback";

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var authUrl = $"https://{auth0Domain}/authorize?"                           +
            $"response_type=code&client_id={Uri.EscapeDataString(clientId)}"        +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"                    +
            $"&scope={Uri.EscapeDataString("openid profile email offline_access")}" +
            $"&audience={Uri.EscapeDataString(audience)}"                           +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        await Console.Out.WriteLineAsync("Opening browser for authentication...");
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var context = await listener.GetContextAsync();
        var code    = context.Request.QueryString["code"];

        const string html = "<html><body><h2>Authentication successful!</h2><p>You can close this window.</p></body></html>";

        var buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType     = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
        listener.Stop();

        if (string.IsNullOrEmpty(code)) {
            Console.Error.WriteLine("Error: No authorization code received.");

            return 1;
        }

        using var http = new HttpClient();

        var tokenResponse = await http.PostAsync(
            $"https://{auth0Domain}/oauth/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string> {
                    ["grant_type"]    = "authorization_code",
                    ["client_id"]     = clientId,
                    ["code"]          = code,
                    ["redirect_uri"]  = redirectUri,
                    ["code_verifier"] = verifier
                }
            )
        );

        if (!tokenResponse.IsSuccessStatusCode) {
            Console.Error.WriteLine($"Error: {await tokenResponse.Content.ReadAsStringAsync()}");

            return 1;
        }

        var json = (await tokenResponse.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.Auth0TokenResponse))!;

        var username = "unknown";

        if (json.IdToken is not null) {
            var payload = json.IdToken.Split('.')[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claims = JsonSerializer.Deserialize(Convert.FromBase64String(payload), KapacitorJsonContext.Default.Auth0IdTokenClaims);
            username = claims?.Nickname ?? "unknown";
        }

        await TokenStore.SaveAsync(
            new() {
                AccessToken    = json.AccessToken,
                RefreshToken   = json.RefreshToken,
                ExpiresAt      = DateTimeOffset.UtcNow.AddSeconds(json.ExpiresIn),
                GitHubUsername = username,
                Provider       = AuthProvider.Auth0,
                Auth0Domain    = auth0Domain,
                ClientId       = clientId
            }
        );

        await Console.Out.WriteLineAsync($"Logged in as {username}");

        return 0;
    }

    internal readonly record struct CallbackResult(string? Code, string? Error);

    internal static string BuildGitHubAuthorizeUrl(
            string clientId, string redirectUri, string state, string codeChallenge) =>
        "https://github.com/login/oauth/authorize?"             +
        $"client_id={Uri.EscapeDataString(clientId)}"           +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"    +
        $"&state={Uri.EscapeDataString(state)}"                 +
        $"&scope={Uri.EscapeDataString("read:user read:org")}"  +
        $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"+
        "&code_challenge_method=S256"                           +
        "&response_type=code";

    internal static CallbackResult ParseCallback(string queryString, string expectedState) {
        var qs    = queryString.TrimStart('?');
        var parts = qs.Split('&', StringSplitOptions.RemoveEmptyEntries);
        string? code = null, state = null, error = null;

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

        if (error is not null)        return new(null, error);
        if (state != expectedState)   return new(null, "state_mismatch");
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

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

public static class HttpClientExtensions {
    /// <summary>
    /// Creates an HttpClient with a Bearer token from the local token store.
    /// Checks auth discovery first — if the server uses "None" provider, skips auth entirely.
    /// All CLI commands that call the Capacitor server should use this
    /// instead of <c>new HttpClient()</c>.
    /// </summary>
    public static async Task<HttpClient> CreateAuthenticatedClientAsync(string? baseUrl = null, CancellationToken ct = default) {
        var client = new HttpClient();

        baseUrl ??= AppConfig.ResolvedServerUrl ?? Environment.GetEnvironmentVariable("KAPACITOR_URL") ?? "http://localhost:5108";
        var provider = await DiscoverProviderAsync(baseUrl, ct);

        if (provider == "None") {
            return client; // No auth needed
        }

        var tokens = await TokenStore.GetValidTokensAsync();

        if (tokens is not null) {
            client.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        } else {
            var stored = await TokenStore.LoadAsync();

            if (stored is not null) {
                await Console.Error.WriteLineAsync("Authentication token has expired. Run 'kapacitor login' to re-authenticate.");
            } else {
                await Console.Error.WriteLineAsync("Not authenticated. Run 'kapacitor login' to authenticate.");
            }
        }

        return client;
    }

    static string? cachedProvider;

    public static async Task<string> DiscoverProviderAsync(string baseUrl, CancellationToken ct = default) {
        if (cachedProvider is not null) {
            return cachedProvider;
        }

        // Hooks call this BEFORE any *WithRetryAsync, so a legacy scheme-less
        // server_url would crash here first if we did not guard. Fail fast with
        // the same actionable message the retry guards print.
        EnsureAbsolute(baseUrl);

        using var http = new HttpClient();

        try {
            var response = await http.GetAsync($"{baseUrl}/auth/config", ct);

            if (response.IsSuccessStatusCode) {
                var config   = await response.Content.ReadFromJsonAsync(KapacitorJsonContext.Default.AuthDiscoveryResponse, ct);
                var provider = config?.Provider ?? "None";
                cachedProvider = provider; // Only cache successful discovery

                return provider;
            }
        } catch {
            // Server unreachable — don't cache, try tokens as fallback.
            // Catches both HttpRequestException (connection failures) and
            // OperationCanceledException (caller's CT fired — fall through to
            // local-token fallback rather than bubbling the cancellation).
        }

        // Fallback: try existing tokens (don't cache — allow re-discovery next time)
        return (await TokenStore.LoadAsync())?.Provider ?? "None";
    }

    static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxDelay       = TimeSpan.FromSeconds(4);

    const string UnreachableHint =
        "Kurrent Capacitor API cannot be reached, is it running? "                    +
        "Make sure the URL is correctly configured and the service is running. "      +
        "Check https://github.com/kurrent-io/claude-remember#setup for instructions." +
        "\rError connecting to: ";

    internal const string SchemeMissingHint =
        "server_url is missing a scheme. Run: kapacitor config set server_url https://<host>";

    /// <summary>
    /// Pure test seam for <see cref="EnsureAbsolute"/>. Returns <c>true</c> only
    /// for absolute http/https URLs.
    /// </summary>
    public static bool IsAcceptableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https";

    /// <summary>
    /// Fails fast with an actionable message if <paramref name="url"/> is not
    /// an absolute http/https URL. Called by every <c>*WithRetryAsync</c>
    /// extension so a legacy scheme-less config produces a clean exit instead
    /// of an unhandled <see cref="InvalidOperationException"/> from
    /// <see cref="HttpClient.PrepareRequestMessage"/>.
    /// </summary>
    static void EnsureAbsolute(string url) {
        if (IsAcceptableUrl(url)) return;
        Console.Error.WriteLine(SchemeMissingHint);
        Environment.Exit(2);
    }

    extension(HttpClient client) {
        public Task<HttpResponseMessage> PostWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            ) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.PostAsync(url, content, ct), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> GetWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.GetAsync(url, ct), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> PutWithRetryAsync(
                string            url,
                HttpContent       content,
                TimeSpan?         timeout = null,
                CancellationToken ct      = default
            ) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.PutAsync(url, content, ct), timeout ?? DefaultTimeout, ct);
        }

        public Task<HttpResponseMessage> DeleteWithRetryAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) {
            EnsureAbsolute(url);
            return SendWithRetryAsync(() => client.DeleteAsync(url, ct), timeout ?? DefaultTimeout, ct);
        }

        /// <summary>
        /// Single-attempt POST with a hard per-call timeout. No retry, no
        /// backoff. Used by hook-path call sites where retries would burst
        /// the shared dispatcher budget. <paramref name="ct"/> is honoured;
        /// expiry of <paramref name="timeout"/> surfaces as
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        public async Task<HttpResponseMessage> PostOnceAsync(
                string            url,
                HttpContent       content,
                TimeSpan          timeout,
                CancellationToken ct = default
            ) {
            EnsureAbsolute(url);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await client.PostAsync(url, content, linkedCts.Token);
        }

        /// <summary>Single-attempt GET — see <see cref="PostOnceAsync"/>.</summary>
        public async Task<HttpResponseMessage> GetOnceAsync(
                string            url,
                TimeSpan          timeout,
                CancellationToken ct = default
            ) {
            EnsureAbsolute(url);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            return await client.GetAsync(url, linkedCts.Token);
        }
    }

    /// <summary>
    /// Writes a structured JSON error to stderr for when the API is unreachable after all retries.
    /// </summary>
    public static void WriteUnreachableError(string baseUrl, HttpRequestException ex) {
        Console.Error.WriteLine($"{UnreachableHint} {baseUrl} {ex.Message}");
    }

    /// <summary>
    /// Checks if the response is a 401 and prints the server's error message.
    /// Returns true if the response was a 401 (caller should return early).
    /// </summary>
    public static async Task<bool> HandleUnauthorizedAsync(HttpResponseMessage response) {
        if (response.StatusCode != HttpStatusCode.Unauthorized) {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync();

        try {
            using var doc     = JsonDocument.Parse(body);
            var       message = doc.RootElement.Str("message");
            await Console.Error.WriteLineAsync(message ?? "Authentication failed. Run 'kapacitor login' to re-authenticate.");
        } catch {
            await Console.Error.WriteLineAsync("Authentication failed. Run 'kapacitor login' to re-authenticate.");
        }

        return true;
    }

    static async Task<HttpResponseMessage> SendWithRetryAsync(Func<Task<HttpResponseMessage>> send, TimeSpan timeout, CancellationToken ct) {
        var sw      = Stopwatch.StartNew();
        var delayMs = 250;

        while (true) {
            try {
                return await send();
            } catch (HttpRequestException) when (!ct.IsCancellationRequested && sw.Elapsed < timeout) {
                await Task.Delay(delayMs, ct);
                delayMs = Math.Min(delayMs * 2, (int)MaxDelay.TotalMilliseconds);
            }
        }
    }
}

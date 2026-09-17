using System.Net.Http.Json;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// WorkOS — the only host that is ever sent a refresh token or a machine secret. It carries neither
/// our credential nor our observation headers: a version tag we mint describes our own server.
///
/// <para>Takes the factory rather than an <see cref="HttpClient"/>, because the token store holds one
/// of these for the process's life and a client handed over at construction would freeze the handler
/// it was built with.</para>
///
/// <para>The mint and the refresh <b>report rather than throw</b>, cancellation aside: both sit under
/// client construction, whose contract is to hand back an auth outcome — a hook that cannot
/// authenticate exits quietly rather than stack-tracing into a transcript. The sign-in legs do not,
/// because a sign-in is interactive and a transport failure there is worth saying out loud.</para>
/// </summary>
public sealed class WorkOSClient(
        IHttpClientFactory httpFactory,
        TimeProvider       time,
        TimeSpan?          refreshTimeout = null,
        TimeSpan?          replayBudget   = null,
        TimeSpan?          replayBackoff  = null) {
    /// <summary>AuthKit's API host. The machine mint posts elsewhere — see <see cref="MachineAuth.DefaultTokenUrl"/>.</summary>
    public const string ApiBase = "https://api.workos.com";

    // Per-attempt deadline. The shared HttpClient carries the 100 s default, and a refresh runs under
    // the cross-process lock, so a stalled WorkOS would otherwise hold every peer's auth for that long.
    readonly TimeSpan _refreshTimeout = refreshTimeout ?? TimeSpan.FromSeconds(5);

    // WorkOS retires a refresh token on use but honours a replay of it for 30 s, answering with the
    // same rotated pair. Replays run only while the next attempt can still start inside that window,
    // measured from the first send: 20 s plus one 5 s attempt keeps every replay under the 30 s.
    readonly TimeSpan _replayBudget  = replayBudget  ?? TimeSpan.FromSeconds(20);
    readonly TimeSpan _replayBackoff = replayBackoff ?? TimeSpan.FromSeconds(1);

    /// <summary>
    /// The longest <see cref="RefreshAsync"/> runs: no attempt starts unless it can finish inside this
    /// budget. A peer waiting on the token lock must wait at least this long, or it gives up on a
    /// holder that is about to persist a fresh token.
    /// </summary>
    public TimeSpan RefreshBudget => _replayBudget;

    /// <summary>
    /// Exchanges a rotating refresh token for a fresh access token, classifying the outcome so a caller
    /// can tell a token WorkOS refused apart from a request that never landed.
    ///
    /// <para>A lost reply, a 5xx/408/429, or an unreadable success body is replayed with the same token
    /// while the next attempt can still complete inside <see cref="RefreshBudget"/>. Inside WorkOS's
    /// replay window a replay is idempotent — it returns the rotated pair the first exchange minted —
    /// and a replay that lands outside it is refused as <c>invalid_grant</c>, so the budget is
    /// re-checked after every backoff, never only before it. A 4xx is never replayed: WorkOS
    /// understood the token and refused it. Reports rather than throws, cancellation aside.</para>
    /// </summary>
    public async Task<WorkOSRefreshResult> RefreshAsync(
            string clientId, string refreshToken, CancellationToken ct) {
        var started  = time.GetTimestamp();
        var consumed = false;

        bool AnotherAttemptFits() => time.GetElapsedTime(started) + _refreshTimeout <= _replayBudget;

        while (true) {
            var (outcome, body) = await RefreshOnceAsync(clientId, refreshToken, ct);

            switch (outcome) {
                case Attempt.Rotated: return new(WorkOSRefreshOutcome.Rotated, body);
                case Attempt.Refused: return new(WorkOSRefreshOutcome.Rejected, null);
            }

            // Any success WorkOS sent, readable or not, proves the token is spent — whatever the later
            // replays do, once they run out the outcome is Rejected, never a retryable failure.
            consumed |= outcome == Attempt.Unreadable;

            if (AnotherAttemptFits()) {
                await Task.Delay(_replayBackoff, time, ct);
            }

            // A suspended machine or a starved scheduler can stretch the delay past the window.
            if (!AnotherAttemptFits()) {
                return new(consumed ? WorkOSRefreshOutcome.Rejected : WorkOSRefreshOutcome.TransportFailed, null);
            }
        }
    }

    enum Attempt { Rotated, Refused, Transient, Unreadable }

    async Task<(Attempt Outcome, WorkOSAuthResponse? Body)> RefreshOnceAsync(
            string clientId, string refreshToken, CancellationToken ct) {
        // The deadline cancels only the linked token, so a timeout is classified below while the
        // caller's own cancellation still propagates.
        using var timeout  = new CancellationTokenSource(_refreshTimeout, time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        HttpResponseMessage response;
        try {
            response = await PostFormAsync(AuthenticateUrl, new() {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = clientId,
                ["refresh_token"] = refreshToken
            }, deadline.Token);
        } catch when (!ct.IsCancellationRequested) {
            return (Attempt.Transient, null);
        }

        using (response) {
            if (!response.IsSuccessStatusCode) {
                return (IsTransientStatus((int)response.StatusCode) ? Attempt.Transient : Attempt.Refused, null);
            }

            WorkOSAuthResponse? body;
            try {
                body = await response.Content.ReadFromJsonAsync(
                    CapacitorJsonContext.Default.WorkOSAuthResponse, deadline.Token);
            } catch when (!ct.IsCancellationRequested) {
                return (Attempt.Unreadable, null);
            }

            return body is null ? (Attempt.Unreadable, null) : (Attempt.Rotated, body);
        }
    }

    static bool IsTransientStatus(int status) => status >= 500 || status is 408 or 429;

    /// <summary>
    /// Opens a device grant. It takes no organization: the human picks one at the AuthKit screen, so
    /// a caller that needs a particular one corrects it afterwards with
    /// <see cref="SwitchOrganizationAsync"/>.
    /// </summary>
    public Task<HttpResponseMessage> AuthorizeDeviceAsync(string clientId, CancellationToken ct) =>
        PostFormAsync($"{ApiBase}/user_management/authorize/device", new() { ["client_id"] = clientId }, ct);

    /// <summary>One poll of the device grant. The RFC 8628 loop that drives this lives with the flow.</summary>
    public Task<HttpResponseMessage> PollForTokenAsync(Dictionary<string, string> form, CancellationToken ct) =>
        PostFormAsync(AuthenticateUrl, form, ct);

    /// <summary>
    /// Moves an authenticated session onto an organization, or null when WorkOS refused. The resulting
    /// refresh token stays bound to that organization, so later refreshes need no id of their own.
    ///
    /// <para>A transport failure surfaces rather than reading as a refusal: the caller renders null as
    /// "you signed in to the wrong workspace", which a network blip has not established.</para>
    /// </summary>
    public async Task<WorkOSAuthResponse?> SwitchOrganizationAsync(
            string clientId, string refreshToken, string organizationId, CancellationToken ct) {
        using var response = await PostFormAsync(AuthenticateUrl, new() {
            ["grant_type"]      = "refresh_token",
            ["client_id"]       = clientId,
            ["refresh_token"]   = refreshToken,
            ["organization_id"] = organizationId
        }, ct);

        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.WorkOSAuthResponse, ct)
            : null;
    }

    /// <summary>
    /// Exchanges a machine credential for a short-lived bearer over <c>client_credentials</c>.
    ///
    /// <para><paramref name="tokenUrl"/> is resolved by the caller, which is also what refuses an
    /// endpoint the credential must not be sent to. It is not the same host as
    /// <see cref="ApiBase"/>, so it arrives per call rather than being pinned here.</para>
    /// </summary>
    public async Task<MachineTokenMint> MintAsync(
            MachineCredential credential, string tokenUrl, CancellationToken ct) {
        try {
            using var http = httpFactory.CreateClient(CapacitorClients.WorkOS);
            using var form = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", credential.ClientId),
                new KeyValuePair<string, string>("client_secret", credential.ClientSecret)
            ]);

            using var response = await http.PostAsync(tokenUrl, form, ct);

            if (!response.IsSuccessStatusCode) {
                // Deliberately does NOT echo the response body. A token endpoint's error body is
                // attacker-influenced and, on some providers, reflects the request — which here contains
                // the secret. The status is the diagnostic; the body is not worth the risk.
                return new(null, 0, $"the machine credential was rejected by {SafeUrl(tokenUrl)} "
                                  + $"(HTTP {(int)response.StatusCode}). Check {MachineAuth.ClientIdVar}/"
                                  + $"{MachineAuth.ClientSecretVar}, or re-issue with 'kcap machine create'.");
            }

            var body = await response.Content.ReadFromJsonAsync(
                CapacitorJsonContext.Default.MachineTokenResponse, ct);

            return string.IsNullOrEmpty(body?.AccessToken)
                ? new(null, 0, $"{SafeUrl(tokenUrl)} returned success with no access_token.")
                : new(body.AccessToken, body.ExpiresIn, null);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Both the URL and the exception message reach stderr, so both are sanitised the same way
            // HttpClientExtensions.RenderUnreachableError does — userinfo dropped from the URL, control
            // characters stripped from the message so a crafted value cannot inject lines into stderr.
            return new(null, 0, $"could not reach {SafeUrl(tokenUrl)}: "
                              + HttpClientExtensions.StripControlCharacters(ex.Message));
        }
    }

    static string AuthenticateUrl => $"{ApiBase}/user_management/authenticate";

    // Accept rides on the request rather than the client, so a caller's own client cannot decide what
    // WorkOS answers with.
    async Task<HttpResponseMessage> PostFormAsync(
            string url, Dictionary<string, string> form, CancellationToken ct) {
        using var http    = httpFactory.CreateClient(CapacitorClients.WorkOS);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) {
            Content = new FormUrlEncodedContent(form)
        };

        request.Headers.Accept.Add(new("application/json"));

        return await http.SendAsync(request, ct);
    }

    // A token URL reaches stderr in three Problem strings. It must never carry userinfo there — a
    // KCAP_WORKOS_TOKEN_URL of https://id:secret@host would otherwise print the secret — and it must
    // not carry control characters that inject lines.
    static string SafeUrl(string url) => UnusableUrlDiagnostic.Sanitize(url);
}

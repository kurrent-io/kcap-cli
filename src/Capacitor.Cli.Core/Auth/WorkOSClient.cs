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
public sealed class WorkOSClient(IHttpClientFactory httpFactory) {
    /// <summary>AuthKit's API host. The machine mint posts elsewhere — see <see cref="MachineAuth.DefaultTokenUrl"/>.</summary>
    public const string ApiBase = "https://api.workos.com";

    /// <summary>
    /// Exchanges a rotating refresh token for a fresh access token, classifying the outcome so a caller
    /// can tell a token WorkOS refused apart from a request that never landed.
    ///
    /// <para>Sent once — never retried. WorkOS consumes the refresh token on every use, so re-sending
    /// one after a lost response presents a token it has already spent, which trips reuse detection and
    /// revokes the whole family. A <see cref="WorkOSRefreshOutcome.Rejected"/> is any non-success status
    /// (an <c>invalid_grant</c> among them); <see cref="WorkOSRefreshOutcome.TransportFailed"/> is an
    /// exception or an unreadable body, where the token was not spent. Reports rather than throws,
    /// cancellation aside.</para>
    /// </summary>
    public async Task<WorkOSRefreshResult> RefreshAsync(
            string clientId, string refreshToken, CancellationToken ct) {
        try {
            using var response = await PostFormAsync(AuthenticateUrl, new() {
                ["grant_type"]    = "refresh_token",
                ["client_id"]     = clientId,
                ["refresh_token"] = refreshToken
            }, ct);

            if (!response.IsSuccessStatusCode) {
                // A 4xx means WorkOS understood and refused the refresh token (a 400 invalid_grant on
                // a consumed/revoked token is the usual one) — terminal, only `kcap login` repairs it.
                // A 5xx/408/429 is the server faltering, not the token being bad, so it reads as a
                // transport failure the caller may retry with the same still-live token.
                return IsTransientStatus((int)response.StatusCode)
                    ? new(WorkOSRefreshOutcome.TransportFailed, null)
                    : new(WorkOSRefreshOutcome.Rejected, null);
            }

            var body = await response.Content.ReadFromJsonAsync(
                CapacitorJsonContext.Default.WorkOSAuthResponse, ct);

            return body is null
                ? new(WorkOSRefreshOutcome.TransportFailed, null)
                : new(WorkOSRefreshOutcome.Rotated, body);
        } catch when (!ct.IsCancellationRequested) {
            return new(WorkOSRefreshOutcome.TransportFailed, null);
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

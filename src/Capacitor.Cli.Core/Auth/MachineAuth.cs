namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// A machine credential read from the environment: the public client id and its secret.
/// </summary>
public sealed record MachineCredential(string ClientId, string ClientSecret);

/// <summary>
/// The machine credential a headless runner carries, resolved once at the composition root, plus
/// where to exchange it.
///
/// <para>A runner has no profile and no token store — it is a fresh container with two environment
/// variables. Everything else it needs it already has: the server comes from <c>KCAP_URL</c>, and
/// provider discovery over <c>/auth/config</c> needs no credential.</para>
///
/// <para>A value rather than a reader, so the process that resolves the credential and the process
/// that explains the resolution to the operator cannot answer differently — <c>kcap status</c>
/// describes the same instance the credential lane picked from.</para>
/// </summary>
public sealed record MachineAuth(string? ClientId, string? ClientSecret, string? TokenUrlOverride) {
    public const string ClientIdVar     = "KCAP_CLIENT_ID";
    public const string ClientSecretVar = "KCAP_CLIENT_SECRET";
    public const string TokenUrlVar     = "KCAP_WORKOS_TOKEN_URL";

    /// <summary>
    /// WorkOS AuthKit's OAuth2 token endpoint.
    ///
    /// <para>Hardcoded, with an env override, for the same reason
    /// <see cref="AuthEndpoints.DefaultProxyUrl"/> is: it is one value for the whole fleet, since every
    /// tenant shares a single WorkOS environment and application. It deliberately is NOT derived from
    /// the tenant's <c>/auth/config</c>, because the field that would carry it — <c>authkit_domain</c> —
    /// is blank on every tenant, so deriving it would produce a broken URL on all of them.</para>
    ///
    /// <para>Measured, not assumed: this host answers <c>grant_type=client_credentials</c> with an
    /// OAuth2 credential rejection for bad credentials. <c>api.workos.com/oauth2/token</c> 404s.</para>
    /// </summary>
    public const string DefaultTokenUrl = "https://signin.kcap.ai/oauth2/token";

    /// <summary>No machine credential — the process authenticates as whoever is signed in.</summary>
    public static readonly MachineAuth None = new(null, null, null);

    /// <summary>This process's machine credential. Call once, in <c>Main</c> or the composition
    /// root.</summary>
    public static MachineAuth FromEnvironment() => new(
        Environment.GetEnvironmentVariable(ClientIdVar),
        Environment.GetEnvironmentVariable(ClientSecretVar),
        Environment.GetEnvironmentVariable(TokenUrlVar));

    /// <summary><c>KCAP_WORKOS_TOKEN_URL</c> is an internal dev/test override; not documented for
    /// end users.</summary>
    public string TokenUrl => (TokenUrlOverride ?? DefaultTokenUrl).Trim();

    /// <summary>
    /// The token URL, refusing any override that could exfiltrate the credential.
    ///
    /// <para><c>KCAP_WORKOS_TOKEN_URL</c> is a redirect primitive: the request direction carries the
    /// secret, so anyone who can set one environment variable — a k8s ConfigMap rather than a Secret,
    /// a CI "variable" rather than a "secret" — could point the mint at a host they control and
    /// harvest it. The no-echo rule protects the RESPONSE direction; this protects the request.</para>
    ///
    /// <para><b>https, except loopback.</b> A bare https requirement would be untestable without
    /// trusting a stub's certificate, and loopback is the same carve-out OAuth redirect-URI rules make
    /// for the same reason: a credential cannot leave the machine over 127.0.0.1. Anything else plaintext
    /// is refused rather than silently falling back to the default, because a silent fallback would send
    /// the real credential to the real endpoint while the developer believed they were pointed at a stub.</para>
    ///
    /// <para>Reports rather than throws: this whole path's contract is to return an auth outcome, and a
    /// member that throws would surface as an unhandled exception inside a hook.</para>
    /// </summary>
    public string? TryResolveTokenUrl(out string? problem) {
        var raw = TokenUrl;

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) {
            problem = $"{TokenUrlVar} is not a valid absolute URL ({raw}).";

            return null;
        }

        // `IsLoopback` is HOST-only, so it must be paired with the http scheme — otherwise
        // ftp://127.0.0.1 or ws://localhost would pass, being loopback but not a credential-safe
        // POST target.
        var httpsAnywhere  = uri.Scheme == Uri.UriSchemeHttps;
        var httpOnLoopback = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;

        if (httpsAnywhere || httpOnLoopback) {
            problem = null;

            return raw;
        }

        problem = $"{TokenUrlVar} must be https (or http on loopback for testing) — refusing to send a "
                + $"machine credential to {uri.Scheme}://{uri.Host}.";

        return null;
    }

    bool HasId     => !string.IsNullOrWhiteSpace(ClientId);
    bool HasSecret => !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Presence, never values. A record's synthesized <c>ToString</c> prints every property,
    /// so one interpolation or one structured-log field would put the client secret in a transcript —
    /// and this is a container singleton every lane can reach.</summary>
    public override string ToString() =>
        $"{nameof(MachineAuth)} {{ {nameof(ClientId)} = {Presence(ClientId)}, "
      + $"{nameof(ClientSecret)} = {Presence(ClientSecret)}, "
      + $"{nameof(TokenUrlOverride)} = {Presence(TokenUrlOverride)} }}";

    static string Presence(string? value) => string.IsNullOrWhiteSpace(value) ? "unset" : "set";

    /// <summary>
    /// True when EITHER half is present — i.e. someone intended machine auth. Deliberately not
    /// "both", so a half-configured runner is diagnosed rather than silently falling back to a token
    /// store it does not have and being told to run <c>kcap login</c>, which it cannot do.
    ///
    /// <para><b>Do not widen this.</b> It decides that machine auth applies INSTEAD of the profile, so an
    /// interactive developer with an ambient <c>KCAP_CLIENT_ID</c> is diverted off their own token store
    /// until they unset it. That is survivable because these names are specific to this product and the
    /// failure is loud (they are told which variable is missing) — neither of which would hold for a
    /// looser gate like a bare <c>CLIENT_ID</c> or a "looks like a machine" heuristic.</para>
    /// </summary>
    public bool Intended => HasId || HasSecret;

    /// <summary>
    /// Both halves. Returns null with a <paramref name="problem"/> naming the missing variable when
    /// only one is set.
    /// </summary>
    public MachineCredential? TryRead(out string? problem) {
        if (HasId && HasSecret) {
            problem = null;

            return new(ClientId!.Trim(), ClientSecret!.Trim());
        }

        problem = (HasId, HasSecret) switch {
            (true, false) => $"{ClientIdVar} is set but {ClientSecretVar} is not — a machine needs both.",
            (false, true) => $"{ClientSecretVar} is set but {ClientIdVar} is not — a machine needs both.",
            _             => null
        };

        return null;
    }

    /// <summary>
    /// The one-line <c>kcap status</c> explanation of the auth diversion <see cref="Intended"/>
    /// causes. Null when machine auth is not in play (caller prints nothing).
    ///
    /// <para>Distinguishes the two states <see cref="Intended"/> (either-half) collapses, because they
    /// are not the same to a reader: with BOTH variables present the CLI genuinely records as the
    /// machine instead of the signed-in user; with only ONE the credential is incomplete, so
    /// <see cref="TryRead"/> refuses it and NOTHING records — the diversion still happens (the token
    /// store is bypassed), so <c>kcap login</c> is not the fix. Saying "records as the machine" in
    /// the one-variable case, or letting the profile token-store line then advise <c>kcap login</c>,
    /// would both be false. Names exactly which variable(s) are present so the message is truthful in
    /// every case.</para>
    /// </summary>
    public string? Diversion => (HasId, HasSecret) switch {
        (false, false) => null,
        (true,  true)  => $"machine credential ({ClientIdVar} and {ClientSecretVar} set) — kcap records as the machine, not as your login.",
        (true,  false) => $"machine credential incomplete — {ClientIdVar} is set but {ClientSecretVar} is not. Auth is diverted off your login and will fail until both are set.",
        (false, true)  => $"machine credential incomplete — {ClientSecretVar} is set but {ClientIdVar} is not. Auth is diverted off your login and will fail until both are set.",
    };
}

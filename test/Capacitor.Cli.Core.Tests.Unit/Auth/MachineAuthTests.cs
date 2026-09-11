using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// Machine-credential authentication for headless runners.
///
/// <para>These go through the REAL HTTP exchange against a stub server rather than asserting shapes
/// around it: the thing that has to work is the exchange, including the wire format, which is
/// precisely what would be silently wrong while everything around it looked healthy.</para>
///
/// <para>The credential is a value handed in, the way a composition root hands production one, so
/// nothing here touches the environment and none of it needs exclusion.</para>
/// </summary>
public class MachineAuthTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    // The mint goes over an unconfigured client, which is what the production WorkOS lane resolves to
    // against a stub: no credential of ours, no observation headers.
    static readonly WorkOSClient Workos = new(new PlainHttpClientFactory());

    public void Dispose() => _server.Stop();

    /// <summary>Pointed at the stub's token endpoint, carrying no credential — enough to mint with
    /// one passed to <c>GetTokenAsync</c> directly.</summary>
    MachineAuth StubEndpoint => new(null, null, $"{_server.Urls[0]}/oauth2/token");

    /// <summary>What a headless runner exports: both halves, against the stub.</summary>
    MachineAuth Runner => StubEndpoint with { ClientId = "client_01ABC", ClientSecret = "sekrit" };

    void StubToken(string token, int expiresIn = 3600) =>
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"access_token\":\"{token}\",\"expires_in\":{expiresIn}}}"));

    /// <summary>A container wired the same way production resolves a client, for the sites that go
    /// through <see cref="ICapacitorHttpClient"/> rather than calling the minter directly. Profiles
    /// resolve to none, matching a runner with no profile present.</summary>
    ServiceProvider Container(MachineAuth machine) {
        var services = new ServiceCollection();
        var profiles = Resolutions.None(Config.Root);

        services.AddSingleton(Config.Root);
        services.AddSingleton(profiles);
        services.AddSingleton(new CapacitorServer(_server.Urls[0], Config.Root, profiles));
        services.AddCapacitorHttp(ProfileOverrides.None, machine);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The token endpoint is whatever <c>KCAP_WORKOS_TOKEN_URL</c> names — our own sign-in host by
    /// default, a self-hosted IdP when an operator overrides it. Either may answer a 307 before it
    /// issues anything. The credential travels in the body, which a 307 preserves, so refusing the hop
    /// would fail a mint the endpoint never rejected. Resolved from the container, because the lane's
    /// own configuration is what decides this and a hand-built client would not carry it.
    /// </summary>
    [Test]
    public async Task A_redirect_before_the_token_is_followed_and_still_mints() {

        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(307)
                .WithHeader("Location", $"{_server.Urls[0]}/oauth2/token/regional"));

        _server.Given(Request.Create().WithPath("/oauth2/token/regional").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_after_hop","expires_in":3600}"""));

        using var sp     = Container(Runner);
        using var minter = new MachineTokenProvider(StubEndpoint);

        var result = await minter.GetTokenAsync(
            sp.GetRequiredService<WorkOSClient>(), new MachineCredential("client_01ABC", "sekrit"),
            rejectedToken: null, CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_after_hop");
    }

    // ── Credential reading ─────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Both_variables_present_reads_the_credential() {
        var machine = new MachineAuth("client_01ABC", "sekrit", null);

        await Assert.That(machine.Intended).IsTrue();

        var credential = machine.TryRead(out var problem);

        await Assert.That(problem).IsNull();
        await Assert.That(credential!.ClientId).IsEqualTo("client_01ABC");
        await Assert.That(credential.ClientSecret).IsEqualTo("sekrit");
    }

    /// <summary>
    /// A half-configured runner must be told WHICH half is missing. Silently falling back would advise
    /// `kcap login` — impossible on a runner with no browser and no profile.
    /// </summary>
    [Test]
    [Arguments("client_01ABC", null, "KCAP_CLIENT_SECRET")]
    [Arguments(null, "sekrit", "KCAP_CLIENT_ID")]
    public async Task One_variable_present_is_reported_as_a_problem_naming_the_missing_one(
            string? id, string? secret, string expectedInProblem) {
        var machine = new MachineAuth(id, secret, null);

        await Assert.That(machine.Intended).IsTrue()
            .Because("machine auth was clearly intended, so it must be diagnosed rather than skipped");

        var credential = machine.TryRead(out var problem);

        await Assert.That(credential).IsNull();
        await Assert.That(problem).IsNotNull();
        await Assert.That(problem!).Contains(expectedInProblem);
    }

    [Test]
    public async Task Neither_variable_present_is_not_machine_auth_at_all() {
        await Assert.That(MachineAuth.None.Intended).IsFalse();
        await Assert.That(MachineAuth.None.TryRead(out var problem)).IsNull();
        await Assert.That(problem).IsNull()
            .Because("an ordinary interactive user is not a misconfigured machine");
    }

    /// <summary>The credential must not be printable. The guard belongs on the type, not on the
    /// discipline of every caller that could interpolate it or hand it to a structured log.</summary>
    [Test]
    public async Task Formatting_the_credential_reports_presence_and_never_the_secret() {
        var printed = $"{new MachineAuth("client_01ABC", "sekrit", "https://idp.test/oauth2/token")}";

        await Assert.That(printed).DoesNotContain("sekrit");
        await Assert.That(printed).DoesNotContain("client_01ABC");
        await Assert.That(printed).DoesNotContain("idp.test");
        await Assert.That(printed).Contains("set");
        await Assert.That($"{MachineAuth.None}").Contains("unset");
    }

    // ── The exchange itself ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The load-bearing test: a real POST, and the WIRE FORMAT asserted. Measured against the live
    /// WorkOS endpoint too, which answers this exact form with a credential rejection rather than
    /// `unsupported_grant_type`.
    /// </summary>
    [Test]
    public async Task Minting_posts_client_credentials_form_and_returns_the_token() {
        StubToken("tok_minted");

        using var minter = new MachineTokenProvider(StubEndpoint);
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "sekrit"), rejectedToken: null, CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_minted");

        var requests = _server.LogEntries.ToList();

        await Assert.That(requests.Count).IsEqualTo(1);

        var body = requests[0].RequestMessage.Body ?? "";

        await Assert.That(body).Contains("grant_type=client_credentials");
        await Assert.That(body).Contains("client_id=client_01ABC");
        await Assert.That(body).Contains("client_secret=sekrit");

        // Some token endpoints reject other content types with misleading errors, so pin it.
        var contentType = requests[0].RequestMessage.Headers!["Content-Type"].First();

        await Assert.That(contentType).Contains("application/x-www-form-urlencoded");
    }

    /// <summary>Second call reuses the cached token — no second mint.</summary>
    [Test]
    public async Task A_cached_token_is_reused_without_a_second_request() {
        StubToken("tok_cached");

        var credential = new MachineCredential("client_01ABC", "sekrit");

        using var minter = new MachineTokenProvider(StubEndpoint);

        await minter.GetTokenAsync(Workos, credential, null, CancellationToken.None);
        var second = await minter.GetTokenAsync(Workos, credential, null, CancellationToken.None);

        await Assert.That(second.Token).IsEqualTo("tok_cached");
        await Assert.That(_server.LogEntries.Count).IsEqualTo(1)
            .Because("the whole point of the in-memory cache is not re-minting per call");
    }

    /// <summary>
    /// A cache hit must not queue behind an in-flight mint. The gate is one process-wide semaphore, so
    /// checking the cache behind it makes every caller in the process wait on whichever one is talking
    /// to WorkOS. The blocked mint really is holding the gate here — otherwise this proves nothing.
    /// </summary>
    [Test]
    public async Task A_cache_hit_does_not_wait_for_an_in_flight_mint() {
        StubToken("tok_A");

        var warm = new MachineCredential("client_A", "s");

        // Shared across every call below: the cache and the gate this test exercises are both
        // per-instance, so a fresh minter per call would prove nothing about either.
        using var minter = new MachineTokenProvider(StubEndpoint);

        await minter.GetTokenAsync(Workos, warm, null, CancellationToken.None);

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        using var handler  = new BlockingMint(entered, release.Task);
        var       blocking = new WorkOSClient(new PlainHttpClientFactory(handler));

        // A second client id cannot be served from the cache, so this call reaches the mint and parks
        // there, holding the gate for as long as the test wants it held.
        var minting = minter.GetTokenAsync(
                blocking, new MachineCredential("client_B", "s"), null, CancellationToken.None);

        try {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            var hit = await minter.GetTokenAsync(Workos, warm, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(hit.Token).IsEqualTo("tok_A");
        }
        finally {
            release.TrySetResult();
            await minting;
        }
    }

    /// <summary>
    /// A 401 from the server comes back as `rejectedToken`, and that must force a re-mint. Without it a
    /// revoked-then-reissued credential would keep serving the dead token until its clock ran out.
    /// </summary>
    [Test]
    public async Task A_rejected_token_forces_a_fresh_mint() {
        StubToken("tok_first");

        var credential = new MachineCredential("client_01ABC", "sekrit");

        using var minter = new MachineTokenProvider(StubEndpoint);
        var       first  = await minter.GetTokenAsync(Workos, credential, null, CancellationToken.None);

        await Assert.That(first.Token).IsEqualTo("tok_first");

        var refreshed = await minter.GetTokenAsync(Workos, credential, rejectedToken: first.Token, CancellationToken.None);

        await Assert.That(refreshed.Token).IsNotNull();
        await Assert.That(_server.LogEntries.Count).IsEqualTo(2)
            .Because("the cached token was the one the server refused, so it had to be re-minted");
    }

    /// <summary>
    /// A rejection reports a problem — and the problem must NOT carry the secret. A token endpoint's
    /// error body is attacker-influenced and can reflect the request, which contains the secret.
    /// </summary>
    [Test]
    public async Task A_rejected_credential_reports_a_problem_without_leaking_the_secret() {
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                // A hostile/naive endpoint reflecting the request back at us.
                .WithBody("{\"error\":\"unauthorized\",\"echo\":\"client_secret=hunter2-the-secret\"}"));

        using var minter = new MachineTokenProvider(StubEndpoint);
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "hunter2-the-secret"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem).IsNotNull();
        await Assert.That(result.Problem!).Contains("401");
        await Assert.That(result.Problem!).DoesNotContain("hunter2-the-secret")
            .Because("the response body is never echoed — it can reflect the credential");
    }

    /// <summary>Success with no access_token must fail, not hand back an empty bearer.</summary>
    [Test]
    public async Task A_success_response_with_no_token_is_a_failure() {
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("{\"expires_in\":3600}"));

        using var minter = new MachineTokenProvider(StubEndpoint);
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("no access_token");
    }

    // ── The wiring: does an authenticated client actually carry the bearer? ─────────────────────

    /// <summary>
    /// End-to-end through the client-construction choke point every authenticated CLI call uses: the
    /// only test that proves the machine branch is REACHED and the header actually attached, rather
    /// than that the pieces around it have the right shape.
    /// </summary>
    [Test]
    public async Task An_authenticated_client_carries_the_minted_bearer_with_no_profile_present() {
        StubToken("tok_wired");

        // A runner's server is discovered over unauthenticated /auth/config — no profile, no token store.
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp      = Container(Runner);
        var       attempt = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();
        using var client  = attempt.Client;

        await Assert.That(attempt.Status).IsEqualTo(AuthStatus.Ok);

        // The bearer is applied by a handler on send, not stamped onto DefaultRequestHeaders, so only
        // a real request on the wire proves the client carries it.
        using var response = await client.GetAsync($"{_server.Urls[0]}/ping");

        var sent = _server.LogEntries.Single(e => e.RequestMessage.Path == "/ping").RequestMessage;

        await Assert.That(sent.Headers!["Authorization"].Single()).IsEqualTo("Bearer tok_wired");

        // Without this the test would still pass if the branch moved ahead of provider discovery: it
        // would be checking the final header, not that a mint actually happened.
        var mints = _server.LogEntries.Count(e => e.RequestMessage.Path.Contains("/oauth2/token"));

        await Assert.That(mints).IsEqualTo(1);
    }

    /// <summary>
    /// The same path with a half-configured credential reports NotAuthenticated rather than silently
    /// producing an unauthenticated client that would 401 with no explanation.
    /// </summary>
    [Test]
    public async Task A_half_configured_runner_reports_not_authenticated() {

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));

        using var sp      = Container(StubEndpoint with { ClientId = "client_01ABC" }); // secret missing
        var       attempt = await sp.GetRequiredService<ICapacitorHttpClient>().ForHookAsync();
        using var client  = attempt.Client;

        await Assert.That(attempt.Status).IsEqualTo(AuthStatus.NotAuthenticated);
        await Assert.That(attempt.Usable).IsFalse();
    }
    // ── The token-URL override is a redirect primitive ─────────────────────────────────────────

    /// <summary>
    /// The token URL points the mint at a host of the operator's choosing, and the REQUEST carries the
    /// secret. Plaintext non-loopback must be refused — and refused rather than silently falling back
    /// to the default, which would send the real credential to the real endpoint while the developer
    /// believed they were pointed at a stub.
    /// </summary>
    [Test]
    public async Task A_plaintext_non_loopback_token_url_is_refused_without_sending_the_credential() {
        using var minter = new MachineTokenProvider(
            new MachineAuth(null, null, "http://evil.example.com/oauth2/token"));
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("https");
        await Assert.That(result.Problem!).DoesNotContain("sekrit");
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0)
            .Because("the credential must not leave the process at all when the endpoint is untrusted");
    }

    /// <summary>
    /// The loopback carve-out is for http ONLY. `Uri.IsLoopback` is host-only, so an odd-scheme
    /// loopback URL (ftp/ws/file) must NOT be admitted just for being loopback — it is not a
    /// credential-safe POST target.
    /// </summary>
    [Test]
    [Arguments("ftp://127.0.0.1/oauth2/token")]
    [Arguments("ws://localhost/oauth2/token")]
    public async Task An_odd_scheme_loopback_token_url_is_refused(string url) {
        using var minter = new MachineTokenProvider(new MachineAuth(null, null, url));
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("https");
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    /// <summary>Loopback over http is the deliberate carve-out — a credential cannot leave the machine.</summary>
    [Test]
    public async Task A_loopback_http_token_url_is_allowed_so_stubs_stay_testable() {
        StubToken("tok_loopback");

        using var minter = new MachineTokenProvider(StubEndpoint);
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_loopback");
    }

    [Test]
    public async Task A_malformed_token_url_is_refused() {
        using var minter = new MachineTokenProvider(new MachineAuth(null, null, "not-a-url"));
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains(MachineAuth.TokenUrlVar);
    }

    /// <summary>
    /// The cache is keyed on client id AND token URL, so a second credential does not receive the
    /// first one's token.
    /// </summary>
    [Test]
    public async Task A_different_credential_does_not_receive_the_cached_token() {
        StubToken("tok_for_A");

        using var minter = new MachineTokenProvider(StubEndpoint);
        var       a      = await minter.GetTokenAsync(Workos, new MachineCredential("client_A", "s"), null, CancellationToken.None);

        await Assert.That(a.Token).IsEqualTo("tok_for_A");

        var b = await minter.GetTokenAsync(Workos, new MachineCredential("client_B", "s"), null, CancellationToken.None);

        await Assert.That(_server.LogEntries.Count).IsEqualTo(2)
            .Because("a different client id must mint its own token, not inherit the cached one");
        await Assert.That(b.Token).IsNotNull();
    }

    // ── Automatic 401 recovery on the constructed client ───────────────────────────────────────

    /// <summary>
    /// A token revoked mid-life must not produce repeated 401s. This drives a real request through the
    /// constructed client: the token endpoint issues one token, the API 401s it once, and the client
    /// must re-mint and resend WITHOUT the caller threading anything back.
    /// </summary>
    [Test]
    public async Task An_authenticated_client_automatically_re_mints_on_a_401() {

        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"provider\":\"workos\",\"client_id\":\"client_01TENANT\",\"authkit_domain\":\"\",\"organization_id\":\"org_01T\"}"));

        // Two tokens in sequence from the mint endpoint, via a WireMock scenario state machine.
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .InScenario("mint").WhenStateIs(null!).WillSetStateTo("second")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"tok_1\",\"expires_in\":3600}"));
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .InScenario("mint").WhenStateIs("second")
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"access_token\":\"tok_2\",\"expires_in\":3600}"));

        // /api/ping: 401 the first token, 200 the second.
        _server.Given(Request.Create().WithPath("/api/ping").UsingGet().WithHeader("Authorization", "Bearer tok_1"))
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/api/ping").UsingGet().WithHeader("Authorization", "Bearer tok_2"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        using var sp = Container(Runner);

        // The interactive lane is the one that installs the retry handler.
        using var client = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();

        using var resp = await client.GetAsync($"{_server.Urls[0]}/api/ping");

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK)
            .Because("the client must re-mint after the first token is rejected and resend");

        var mints = _server.LogEntries.Count(e => e.RequestMessage.Path.Contains("/oauth2/token"));
        await Assert.That(mints).IsEqualTo(2)
            .Because("exactly one re-mint: the initial token plus one recovery");
    }

    // ── Error hygiene: no userinfo, no control chars in the Problem ─────────────────────────────

    /// <summary>
    /// A KCAP_WORKOS_TOKEN_URL carrying userinfo must not print the secret half in the Problem string.
    /// The URL reaches stderr, so it goes through the same Sanitize the rest of the CLI uses.
    /// </summary>
    [Test]
    public async Task A_token_url_with_userinfo_does_not_leak_it_into_the_problem() {
        // Loopback so the scheme check admits it and we reach the mint, which then fails (nothing
        // listening on that path) and builds the Problem string containing the URL.
        using var minter = new MachineTokenProvider(
            new MachineAuth(null, null, "http://id:supersecret@127.0.0.1:1/oauth2/token"));
        var       result = await minter.GetTokenAsync(
            Workos, new MachineCredential("client_01ABC", "sekrit"), null, CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem).IsNotNull();
        await Assert.That(result.Problem!).DoesNotContain("supersecret")
            .Because("userinfo in the token URL must be dropped before it reaches stderr");
    }

}

/// <summary>
/// Parks inside the mint until released, so a test can pin <c>MachineTokenProvider</c>'s gate open and
/// watch what a concurrent caller does.
/// </summary>
sealed class BlockingMint(TaskCompletionSource entered, Task release) : HttpMessageHandler {
    protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
        entered.TrySetResult();
        await release.WaitAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent("""{"access_token":"tok_B","expires_in":3600}""",
                                        Encoding.UTF8, "application/json"),
        };
    }
}

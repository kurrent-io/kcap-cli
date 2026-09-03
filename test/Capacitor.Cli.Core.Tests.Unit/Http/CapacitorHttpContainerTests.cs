using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>
/// <see cref="CapacitorHttpServices.AddCapacitorHttp"/> assembled for real and driven against a stub
/// server. <c>AddHttpMessageHandler</c> resolves each handler on the FIRST REQUEST, not at container
/// build, so a missing registration cannot be caught by anything short of a real call.
///
/// <para><c>[NotInParallel]</c>: the auth-provider memo and the machine-token cache are process-wide
/// statics, and the machine-credential variables are process-wide environment.</para>
/// </summary>
[NotInParallel]
public class CapacitorHttpContainerTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    EnvScope? _clientId;
    EnvScope? _clientSecret;
    string    _profile = "";

    string Url => _server.Urls[0];

    [Before(Test)]
    public void Isolate() {
        HttpClientExtensions.ResetProviderCacheForTesting();
        MachineTokenProvider.ResetForTesting();

        // Cleared so the credential source picks the token store: a runner's machine credential would
        // otherwise win over it and mint against an endpoint no test here stubs.
        _clientId     = EnvScope.Exclusive(MachineAuth.ClientIdVar, null);
        _clientSecret = EnvScope.Exclusive(MachineAuth.ClientSecretVar, null);
        _profile      = Resolutions.At(Url, Config.Root).Name;
    }

    public void Dispose() {
        _clientId?.Dispose();
        _clientSecret?.Dispose();
        _server.Stop();
        HttpClientExtensions.ResetProviderCacheForTesting();
        MachineTokenProvider.ResetForTesting();
    }

    ServiceProvider Container(string? serverUrl = null) {
        var target   = serverUrl ?? Url;
        var services = new ServiceCollection();

        services.AddSingleton(new CapacitorServer(target, Config.Root, Resolutions.At(target, Config.Root)));
        services.AddCapacitorHttp();

        return services.BuildServiceProvider();
    }

    void StubProvider(string provider) =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"provider":"{{provider}}"}"""));

    Task SeedTokenAsync(string accessToken) =>
        new TokenStore(Config.Root).SaveAsync(_profile, new StoredTokens {
            AccessToken    = accessToken,
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = Url,
        });

    /// <summary>A stubbed login: a provider to discover and a token bound to this server.</summary>
    async Task AuthenticateAsync(string accessToken) {
        StubProvider(AuthProvider.GitHubApp);
        await SeedTokenAsync(accessToken);
    }

    WireMock.Logging.ILogEntry RequestTo(string path) =>
        _server.LogEntries.Single(e => e.RequestMessage.Path == path);

    // ── The container itself ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A client from the registered factory reaches the wire, which is the only thing that proves every
    /// handler in the chain is registered: the factory resolves them per request, so an unregistered one
    /// throws here and nowhere earlier.
    /// </summary>
    [Test]
    public async Task Every_handler_in_the_chain_resolves_on_the_first_request() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/ping").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await Assert.That(sp.GetRequiredService<ISessionsApi>()).IsNotNull();

        using var client   = await sp.GetRequiredService<ICapacitorHttpClient>().ForCommandAsync();
        using var response = await client.GetAsync($"{Url}/ping");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    // ── Verb, path and body per call ───────────────────────────────────────────────────────────

    [Test]
    public async Task Deleting_a_session_deletes_its_own_path() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        var result = await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-1");

        await Assert.That(result).IsTypeOf<DeleteSessionResponse.Deleted>();
        await Assert.That(RequestTo("/api/sessions/sess-1").RequestMessage.Method).IsEqualTo("DELETE");
    }

    [Test]
    public async Task Hiding_a_session_puts_a_visibility_of_none() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-2/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().HideSessionAsync("sess-2");

        var sent = RequestTo("/api/sessions/sess-2/visibility").RequestMessage;

        await Assert.That(sent.Method).IsEqualTo("PUT");
        await Assert.That(sent.Body).IsEqualTo("""{"visibility":"none"}""");
    }

    [Test]
    public async Task Setting_a_title_posts_the_session_id_beside_it() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().SetSessionTitleAsync("sess-3", "A better title");

        var sent = RequestTo("/hooks/set-title").RequestMessage;

        await Assert.That(sent.Method).IsEqualTo("POST");
        await Assert.That(sent.Body).IsEqualTo("""{"session_id":"sess-3","title":"A better title"}""");
    }

    /// <summary>A session the server holds nothing for is an outcome, not a failure.</summary>
    [Test]
    public async Task Deleting_an_absent_session_is_not_found_rather_than_a_throw() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/gone").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(404));

        using var sp = Container();

        var result = await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("gone");

        await Assert.That(result).IsTypeOf<DeleteSessionResponse.NotFound>();
    }

    // ── Failures ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_refused_call_throws_carrying_the_status() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-4/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));

        using var sp = Container();
        var       api = sp.GetRequiredService<ISessionsApi>();

        var ex = await Assert.That(async () => await api.HideSessionAsync("sess-4")).Throws<CapacitorApiException>();

        await Assert.That(ex!.Status).IsEqualTo(500);
    }

    /// <summary>
    /// A 401 the server explained is shown as the server explained it: the CLI's own re-auth wording is
    /// a fallback for a body that says nothing, not a replacement for one that does.
    /// </summary>
    [Test]
    public async Task A_401_carries_the_servers_own_message() {
        // No bearer is sent under the None provider, so there is nothing to rotate and the refusal
        // reaches the caller on the first attempt.
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Your seat was revoked. Ask an admin to re-invite you."}"""));

        using var sp  = Container();
        var       api = sp.GetRequiredService<ISessionsApi>();

        var ex = await Assert.That(async () => await api.SetSessionTitleAsync("sess-5", "t"))
            .Throws<CapacitorApiException>();

        await Assert.That(ex!.Status).IsEqualTo(401);
        await Assert.That(ex.Message).IsEqualTo("Your seat was revoked. Ask an admin to re-invite you.");
    }

    /// <summary>
    /// A server that never answered is the same one exception with no status, so a caller needs no
    /// second catch. Slow by construction: the whole 30s retry budget is spent before the transport
    /// failure is final.
    /// </summary>
    [Test]
    public async Task An_unreachable_server_throws_with_no_status_at_all() {
        const string dead = "http://127.0.0.1:1";

        using var sp  = Container(dead);
        var       api = sp.GetRequiredService<ISessionsApi>();

        var ex = await Assert.That(async () => await api.DeleteSessionAsync("sess-6"))
            .Throws<CapacitorApiException>();

        await Assert.That(ex!.Status).IsNull();
        await Assert.That(ex.InnerException).IsTypeOf<HttpRequestException>();
    }

    // ── The handler chain ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_request_carries_the_bearer_and_the_cli_version() {
        await AuthenticateAsync("tok_wired");
        _server.Given(Request.Create().WithPath("/api/sessions/sess-7").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-7");

        var headers = RequestTo("/api/sessions/sess-7").RequestMessage.Headers!;

        await Assert.That(headers["Authorization"].Single()).IsEqualTo("Bearer tok_wired");
        await Assert.That(headers[HttpClientExtensions.CliVersionHeader].Single())
            .IsEqualTo(CapacitorVersion.CurrentDisplay());
    }

    [Test]
    public async Task The_servers_version_header_lands_in_the_store() {
        StubProvider(AuthProvider.None);
        _server.Given(Request.Create().WithPath("/api/sessions/sess-8").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader(HttpClientExtensions.ServerVersionHeader, "0.42.0"));

        using var sp = Container();

        await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-8");

        await Assert.That(ServerVersionStore.Get(Url, Config.Root)).IsEqualTo("0.42.0");
    }

    /// <summary>
    /// Handler order, which nothing but a live 401-then-200 exchange can pin. Recovery is INNERMOST, so
    /// the two handlers outside it see the exchange once: version capture observes only the response the
    /// caller gets — the discarded 401's header must not reach the store — and the observation headers
    /// are stamped once, not again on the resend.
    /// </summary>
    [Test]
    public async Task Recovery_sits_inside_both_outer_handlers() {
        await AuthenticateAsync("tok_1");

        _server.Given(Request.Create().WithPath("/auth/refresh").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_2","expires_in":3600}"""));

        _server.Given(Request.Create().WithPath("/api/sessions/sess-9").UsingDelete()
                    .WithHeader("Authorization", "Bearer tok_1"))
            .RespondWith(Response.Create().WithStatusCode(401)
                .WithHeader(HttpClientExtensions.ServerVersionHeader, "0.1.0-preretry"));

        _server.Given(Request.Create().WithPath("/api/sessions/sess-9").UsingDelete()
                    .WithHeader("Authorization", "Bearer tok_2"))
            .RespondWith(Response.Create().WithStatusCode(200));

        using var sp = Container();

        var result = await sp.GetRequiredService<ISessionsApi>().DeleteSessionAsync("sess-9");

        await Assert.That(result).IsTypeOf<DeleteSessionResponse.Deleted>();

        var attempts = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/api/sessions/sess-9")
            .ToList();

        await Assert.That(attempts.Count).IsEqualTo(2);
        await Assert.That(attempts[1].RequestMessage.Headers!["Authorization"].Single()).IsEqualTo("Bearer tok_2");

        await Assert.That(ServerVersionStore.Get(Url, Config.Root)).IsNull()
            .Because("capture is outside recovery, so it never sees the response recovery threw away");

        var stamped = string.Join(",", attempts[1].RequestMessage.Headers![HttpClientExtensions.CliVersionHeader]);

        await Assert.That(stamped).IsEqualTo(CapacitorVersion.CurrentDisplay())
            .Because("the observation handler is outside recovery, so the resend is not stamped twice");
    }
}

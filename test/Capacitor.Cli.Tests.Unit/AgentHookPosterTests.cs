using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Tests for the shared agent-hook recording POST (AI-993). This is the seam the non-Claude
/// hooks (Codex, Gemini, Copilot, Pi, Kiro, OpenCode) delegate to. Its job is to SKIP a POST
/// that would 401 because auth has lapsed — reporting <see cref="HookPostOutcome.AuthLapsed"/>
/// without touching stderr or the server — while leaving the authenticated success path and
/// the real-failure path (stderr + <see cref="HookPostOutcome.Failed"/>) unchanged.
///
/// The (client, status) factory is injected so the auth outcome is controlled directly and no
/// token store or /auth/config discovery is needed; the POST itself goes to a WireMock server.
/// </summary>
public class AgentHookPosterTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static Func<Task<(HttpClient, AuthStatus)>> Factory(AuthStatus status)
        => () => Task.FromResult((new HttpClient(), status));

    [Test]
    public async Task Expired_auth_skips_the_post_and_reports_AuthLapsed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/codex").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.Expired), _server.Url!, "session-start/codex", "{}", "codex-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.AuthLapsed);

        // The doomed POST must never be sent.
        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/codex").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotAuthenticated_skips_the_post_and_reports_AuthLapsed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.NotAuthenticated), _server.Url!, "session-start/gemini", "{}", "gemini-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.AuthLapsed);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Ok_auth_posts_the_body_and_reports_Posted() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/pi").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.Ok), _server.Url!, "session-start/pi", """{"hello":"world"}""", "pi-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/pi").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(1);
        await Assert.That(hits[0].RequestMessage.Body).IsEqualTo("""{"hello":"world"}""");
    }

    [Test]
    public async Task NoAuthRequired_posts_normally() {
        // "None" provider → the client is usable as-is; behave exactly like authenticated.
        _server.Given(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.NoAuthRequired), _server.Url!, "session-start/opencode", "{}", "opencode-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Posted);

        var hits = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost());
        await Assert.That(hits.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Server_error_reports_Failed() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/kiro").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var outcome = await AgentHookPoster.PostAsync(
            Factory(AuthStatus.Ok), _server.Url!, "session-start/kiro", "{}", "kiro-hook");

        await Assert.That(outcome).IsEqualTo(HookPostOutcome.Failed);
    }

    [Test]
    public async Task IsAuthLapsed_is_true_only_for_expired_or_unauthenticated() {
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.Expired)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.NotAuthenticated)).IsTrue();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.Ok)).IsFalse();
        await Assert.That(AgentHookPoster.IsAuthLapsed(AuthStatus.NoAuthRequired)).IsFalse();
    }
}

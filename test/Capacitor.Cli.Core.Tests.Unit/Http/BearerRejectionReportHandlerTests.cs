using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>
/// A 401 is reported with the server's error code and the refused bearer's subject, issue time and
/// expiry against local time — never the bearer — and the caller still gets the response, body
/// included, exactly as the server sent it.
/// </summary>
public class BearerRejectionReportHandlerTests {
    static readonly DateTimeOffset Now = new(2026, 9, 29, 22, 0, 0, TimeSpan.Zero);

    sealed class Answer(HttpStatusCode status, string body, bool unknownLength = false) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            HttpContent content = unknownLength
                ? new StreamContent(new ForwardOnly(Encoding.UTF8.GetBytes(body)))
                : new StringContent(body, Encoding.UTF8, "application/json");

            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    // A chunked body: no Content-Length, readable once.
    sealed class ForwardOnly(byte[] bytes) : MemoryStream(bytes) {
        public override bool CanSeek => false;
    }

    static string Token(long exp, long iat, string sub) {
        static string B64Url(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64Url("""{"alg":"RS256"}""")}.{B64Url($$"""{"sub":"{{sub}}","exp":{{exp}},"iat":{{iat}}}""")}.sig";
    }

    static async Task<(HttpResponseMessage response, List<string> reports)> SendAsync(
            HttpStatusCode status, string body, string? bearer, bool unknownLength = false) {
        var reports = new List<string>();
        var client  = new HttpClient(new BearerRejectionReportHandler(new FakeTimeProvider(Now), reports.Add) {
            InnerHandler = new Answer(status, body, unknownLength)
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://tenant.example/hubs/sessions/negotiate?negotiateVersion=1");

        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return (await client.SendAsync(request), reports);
    }

    [Test]
    public async Task A_401_reports_the_error_code_and_the_refused_bearers_times() {
        var token = Token(exp: Now.AddSeconds(-90).ToUnixTimeSeconds(), iat: Now.AddMinutes(-6).ToUnixTimeSeconds(), sub: "user_1");

        var (response, reports) = await SendAsync(
            HttpStatusCode.Unauthorized, """{"error":"token_expired","message":"Token has expired."}""", token);

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("401 from POST /hubs/sessions/negotiate:");
        await Assert.That(reports[0]).Contains("server error=token_expired");
        await Assert.That(reports[0]).Contains("sub=user_1");
        await Assert.That(reports[0]).Contains("(-90s from now)");
        await Assert.That(reports[0]).DoesNotContain(token).Because("the bearer itself must never be logged");
        await Assert.That(reports[0]).DoesNotContain("negotiateVersion").Because("a query can carry an access token");

        await Assert.That(await response.Content.ReadAsStringAsync()).Contains("token_expired")
            .Because("reading the error code must leave the body readable for the caller");
    }

    [Test]
    public async Task A_401_with_no_bearer_says_so() {
        var (_, reports) = await SendAsync(HttpStatusCode.Unauthorized, """{"error":"unauthenticated"}""", bearer: null);

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("server error=unauthenticated; no bearer sent");
    }

    [Test]
    public async Task A_401_with_an_unreadable_body_or_bearer_still_reports() {
        var (_, reports) = await SendAsync(HttpStatusCode.Unauthorized, "<html>nope</html>", bearer: "opaque");

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("server error=-; bearer is not a readable JWT");
    }

    [Test]
    [Arguments(HttpStatusCode.OK)]
    [Arguments(HttpStatusCode.Forbidden)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    public async Task Other_statuses_report_nothing(HttpStatusCode status) {
        var (_, reports) = await SendAsync(status, "{}", bearer: "opaque");

        await Assert.That(reports).IsEmpty();
    }

    [Test]
    public async Task A_small_body_of_unknown_length_yields_the_code_and_stays_readable() {
        var (response, reports) = await SendAsync(
            HttpStatusCode.Unauthorized, """{"error":"invalid_token"}""", bearer: null, unknownLength: true);

        await Assert.That(reports[0]).Contains("server error=invalid_token");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("""{"error":"invalid_token"}""");
    }

    [Test]
    public async Task An_oversized_body_of_unknown_length_reaches_the_caller_intact() {
        var body = "{\"error\":\"invalid_token\",\"pad\":\"" + new string('x', 10_000) + "\"}";

        var (response, reports) = await SendAsync(HttpStatusCode.Unauthorized, body, bearer: null, unknownLength: true);

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("server error=-")
            .Because("a body past the cap is not parsed");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(body)
            .Because("the bytes read while probing must be put back in front of the rest");
    }

    [Test]
    public async Task A_bearer_whose_payload_is_not_an_object_still_reports() {
        static string B64Url(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var (_, reports) = await SendAsync(HttpStatusCode.Unauthorized, "{}", bearer: $"{B64Url("{}")}.{B64Url("[]")}.sig");

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("bearer is not a readable JWT");
    }

    [Test]
    public async Task Control_characters_from_the_server_or_the_bearer_cannot_break_the_line() {
        var token = Token(exp: Now.AddMinutes(5).ToUnixTimeSeconds(), iat: Now.ToUnixTimeSeconds(), sub: "user_1\\nFAKE line");

        var (_, reports) = await SendAsync(
            HttpStatusCode.Unauthorized, """{"error":"invalid_token\r\nFAKE: granted"}""", token);

        await Assert.That(reports[0]).DoesNotContain("\n");
        await Assert.That(reports[0]).DoesNotContain("\r");
        await Assert.That(reports[0]).Contains("server error=invalid_token??FAKE: granted");
        await Assert.That(reports[0]).Contains("sub=user_1?FAKE line");
    }

    [Test]
    [Arguments(new[] { "Bearer error=\"invalid_token\"" }, "invalid_token")]
    [Arguments(new[] { "Bearer realm=\"kcap\", error=\"token_expired\", error_description=\"x\"" }, "token_expired")]
    [Arguments(new[] { "Basic realm=\"x\"", "Bearer error=\"invalid_token\"" }, "invalid_token")]
    [Arguments(new[] { "Custom error=\"not_ours\"" }, null)]
    [Arguments(new[] { "Bearer error_description=\"detail, error=fake\", error=\"invalid_token\"" }, "invalid_token")]
    [Arguments(new[] { "Bearer error_description=\"a \\\"quoted, error=fake\\\" b\", error=token_expired" }, "token_expired")]
    [Arguments(new[] { "Bearer" }, null)]
    public async Task The_bearer_challenge_yields_its_error(string[] challenges, string? error) =>
        await Assert.That(BearerRejectionReportHandler.ChallengeError(challenges)).IsEqualTo(error);
}

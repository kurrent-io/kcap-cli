using System.Net;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>The hops the guard follows itself, seen by the handler under it.</summary>
public class LocalDestinationRedirectTests {
    sealed class Hops(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    static HttpResponseMessage Redirect(HttpStatusCode status, string location) =>
        new(status) { Headers = { Location = new Uri(location) } };

    static HttpMessageInvoker Guard(Hops hops) =>
        new(new LocalDestinationHandler(null, null, null, followRedirects: true) { InnerHandler = hops });

    /// <summary>A handler above resends the caller's request on a 401, with a fresh bearer; were it the last hop's
    /// URI, an origin the redirect chose would receive that bearer.</summary>
    [Test]
    public async Task The_callers_request_keeps_its_uri_after_a_redirect() {
        var hops = new Hops(r => r.RequestUri!.Host == "allowed.test"
            ? Redirect(HttpStatusCode.Found, "https://elsewhere.test/x")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://allowed.test/a");

        using var response = await Guard(hops).SendAsync(request, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(request.RequestUri!.Host).IsEqualTo("allowed.test");
        await Assert.That(hops.Seen[1]).IsNotSameReferenceAs(request);
    }

    [Test]
    public async Task A_post_turned_into_a_get_carries_no_chunked_encoding() {
        var hops = new Hops(r => r.RequestUri!.AbsolutePath == "/post"
            ? Redirect(HttpStatusCode.Found, "https://allowed.test/get")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://allowed.test/post") { Content = new StringContent("{}") };
        request.Headers.TransferEncodingChunked = true;

        using var response = await Guard(hops).SendAsync(request, CancellationToken.None);

        await Assert.That(hops.Seen[1].Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(hops.Seen[1].Content).IsNull();
        await Assert.That(hops.Seen[1].Headers.TransferEncodingChunked).IsNotEqualTo(true);
    }
}

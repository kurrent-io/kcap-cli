using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

sealed class Handler(HttpStatusCode status, string body, TimeSpan? retryAfter = null) : HttpMessageHandler {
    public int Calls;
    public string? Uri;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Calls++;
        Uri = request.RequestUri?.ToString();
        var response = new HttpResponseMessage(status) {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (retryAfter is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return Task.FromResult(response);
    }
}

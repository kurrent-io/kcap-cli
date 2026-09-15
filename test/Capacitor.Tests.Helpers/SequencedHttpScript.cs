using System.Net;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// Answers each request with the next step, the last step repeating, and records every request body
/// so a replay can be checked for identity. A step that never completes models a host that accepted
/// the request and went silent, which a real stub cannot do on a deadline shorter than its own startup.
/// </summary>
public sealed class SequencedHttpScript(params Func<CancellationToken, Task<HttpResponseMessage>>[] steps) : HttpMessageHandler {
    int _next;

    public List<string> Bodies { get; } = [];

    public int Count => Bodies.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

        var step = Math.Min(Interlocked.Increment(ref _next) - 1, steps.Length - 1);

        return await steps[step](ct);
    }

    public static Func<CancellationToken, Task<HttpResponseMessage>> Reply(HttpStatusCode code, string body = "") =>
        _ => Task.FromResult(AuthHttp.Status(code, body));

    public static Func<CancellationToken, Task<HttpResponseMessage>> Stall() =>
        async ct => {
            await Task.Delay(Timeout.Infinite, ct);

            throw new InvalidOperationException("a stalled step never answers");
        };
}

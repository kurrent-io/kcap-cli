using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Reports each 401 with what the client can see of it: the server's <c>error</c> code and the
/// presented bearer's subject, issue time and expiry against local time — never the bearer itself.
/// Observes only; the response reaches the caller unchanged. Must sit inside whatever applies the
/// bearer, so the request it sees carries the one the server refused.
/// </summary>
internal sealed class BearerRejectionReportHandler(TimeProvider time, Action<string> report) : DelegatingHandler {
    const int MaxBodyBytes = 4096;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var response = await base.SendAsync(request, ct);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        try {
            report(Describe(request, await ReadErrorCodeAsync(response, ct), time.GetUtcNow()));
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // A diagnostic must never change what the caller gets back.
        }

        return response;
    }

    internal static string Describe(HttpRequestMessage request, string? errorCode, DateTimeOffset now) {
        var bearer = request.Headers.Authorization is { Scheme: var scheme, Parameter: { Length: > 0 } token }
                  && scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? DescribeBearer(token, now)
            : "no bearer sent";

        return $"401 from {request.Method} {request.RequestUri?.AbsolutePath}: server error={errorCode ?? "-"}; {bearer}; local_now={now:o}";
    }

    static string DescribeBearer(string token, DateTimeOffset now) {
        var exp = JwtClaims.TryGetTime(token, "exp");
        var iat = JwtClaims.TryGetTime(token, "iat");
        var sub = JwtClaims.TryGetString(token, "sub");

        if (exp is null && iat is null && sub is null) return "bearer is not a readable JWT";

        var expiresIn = exp is { } e ? $" ({(long)(e - now).TotalSeconds}s from now)" : "";

        return $"bearer sub={sub ?? "-"} iat={iat?.ToString("o") ?? "-"} exp={exp?.ToString("o") ?? "-"}{expiresIn}";
    }

    // The server answers a refused bearer with {"error": "...", "message": "..."}. Buffered so a caller
    // that reads the body still can.
    static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.Content.Headers.ContentLength is > MaxBodyBytes) return null;

        try {
            await response.Content.LoadIntoBufferAsync(MaxBodyBytes, ct);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        } catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException) {
            return null;
        }
    }
}

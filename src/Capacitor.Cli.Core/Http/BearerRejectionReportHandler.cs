using System.Net;
using System.Net.WebSockets;
using System.Text;
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

        var bearer = request.Headers.Authorization is { Scheme: var scheme, Parameter: { Length: > 0 } token }
                  && scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase)
            ? token
            : null;

        try {
            var errorCode = await ReadErrorCodeAsync(response, ct);

            report(Describe(request.Method.Method, request.RequestUri, errorCode, bearer, time.GetUtcNow()));
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // A diagnostic must never change what the caller gets back.
        }

        return response;
    }

    /// <summary>
    /// A SignalR <c>WebSocketFactory</c> body. Over HTTP/1.1 SignalR opens the WebSocket itself, outside
    /// any configured message handler, so a 401 on the upgrade would otherwise go unreported. Sets what
    /// SignalR's own factory sets that the server acts on — the bearer, and <c>X-Requested-With</c>, which
    /// makes cookie auth answer 401 instead of redirecting.
    /// </summary>
    public static async ValueTask<WebSocket> ConnectWebSocketAsync(
            Uri uri, Func<Task<string?>>? accessTokenProvider, TimeProvider time, Action<string> report, CancellationToken ct) {
        var webSocket = new ClientWebSocket();

        webSocket.Options.CollectHttpResponseDetails = true;
        webSocket.Options.SetRequestHeader("X-Requested-With", "XMLHttpRequest");

        var bearer = accessTokenProvider is null ? null : await accessTokenProvider();

        if (!string.IsNullOrWhiteSpace(bearer)) webSocket.Options.SetRequestHeader("Authorization", $"Bearer {bearer}");

        try {
            await webSocket.ConnectAsync(uri, ct);

            return webSocket;
        } catch (WebSocketException) {
            if (webSocket.HttpStatusCode == HttpStatusCode.Unauthorized) {
                try {
                    report(Describe("GET", uri, errorCode: null, string.IsNullOrWhiteSpace(bearer) ? null : bearer, time.GetUtcNow()));
                } catch {
                    // A diagnostic must never change the exception the caller sees.
                }
            }

            webSocket.Dispose();

            throw;
        } catch {
            webSocket.Dispose();

            throw;
        }
    }

    internal static string Describe(string method, Uri? uri, string? errorCode, string? bearer, DateTimeOffset now) =>
        $"401 from {method} {uri?.AbsolutePath}: server error={errorCode ?? "-"}; "
      + $"{(bearer is null ? "no bearer sent" : DescribeBearer(bearer, now))}; local_now={now:o}";

    static string DescribeBearer(string token, DateTimeOffset now) {
        var exp = JwtClaims.TryGetTime(token, "exp");
        var iat = JwtClaims.TryGetTime(token, "iat");
        var sub = JwtClaims.TryGetString(token, "sub");

        if (exp is null && iat is null && sub is null) return "bearer is not a readable JWT";

        var expiresIn = exp is { } e ? $" ({(long)(e - now).TotalSeconds}s from now)" : "";

        return $"bearer sub={sub ?? "-"} iat={iat?.ToString("o") ?? "-"} exp={exp?.ToString("o") ?? "-"}{expiresIn}";
    }

    // The server answers a refused bearer with {"error": "...", "message": "..."}. Reads at most one byte
    // past the cap and puts back everything read, so the caller receives the body intact at any size.
    static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken ct) {
        var original = response.Content;
        var body     = await original.ReadAsStreamAsync(ct);
        var head     = new byte[MaxBodyBytes + 1];
        var read     = 0;

        while (read < head.Length) {
            var n = await body.ReadAsync(head.AsMemory(read), ct);

            if (n == 0) break;

            read += n;
        }

        var complete = read <= MaxBodyBytes;

        HttpContent replacement = complete
            ? new ByteArrayContent(head, 0, read)
            : new StreamContent(new PrefixedStream(head.AsMemory(0, read), body));

        foreach (var header in original.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);

        response.Content = replacement;

        // An oversized body's stream now belongs to the replacement; the original must not close it.
        if (!complete) return null;

        original.Dispose();

        try {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(head, 0, read));

            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        } catch (JsonException) {
            return null;
        }
    }

    // The bytes already read, then the rest of the original stream.
    sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream {
        int _offset;

        public override bool CanRead  => true;
        public override bool CanSeek  => false;
        public override bool CanWrite => false;
        public override long Length   => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            if (_offset < prefix.Length) return CopyPrefix(buffer);

            return rest.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _offset < prefix.Length ? ValueTask.FromResult(CopyPrefix(buffer.Span)) : rest.ReadAsync(buffer, ct);

        int CopyPrefix(Span<byte> buffer) {
            var n = Math.Min(buffer.Length, prefix.Length - _offset);

            prefix.Span.Slice(_offset, n).CopyTo(buffer);
            _offset += n;

            return n;
        }

        protected override void Dispose(bool disposing) {
            if (disposing) rest.Dispose();

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

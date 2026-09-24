using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Commands.Capture.Wire;

namespace Capacitor.Cli.Commands.Capture;

internal sealed class CaptureRepairClient(HttpClient http, string collectionUrl, TimeProvider time) {
    internal const int MaxBatchBytes = 4 * 1024 * 1024;
    internal const int MaxBatchLines = 100;
    static CaptureRepairJsonContext Json => CaptureRepairJsonContext.Default;
    readonly List<CaptureRepairLineRequest> _lines = [];
    int _lineBytes;
    int _ordinal;
    string? _operationUrl;
    public string? LastBatchDigest { get; private set; }

    public async Task RequireCapabilityAsync(CancellationToken ct) {
        using var response = await SendAsync(HttpMethod.Get, collectionUrl, null, true, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new IOException("Capture recovery is unavailable; check session ownership and upgrade the server first.");
        await EnsureSuccessAsync(response, ct);
        var capability = await ReadAsync(response, Json.CaptureRepairCapabilitiesResponse, ct);
        if (capability.ProtocolVersion != 1) throw new IOException("Unsupported capture recovery protocol; upgrade the CLI and server.");
    }

    public async Task<string> PrepareAsync(CaptureRepairSourceRequest[] sources, bool dryRun, CancellationToken ct) {
        var body = JsonSerializer.SerializeToUtf8Bytes(new PrepareCaptureRepairRequest { Sources = sources, DryRun = dryRun }, Json.PrepareCaptureRepairRequest);
        using var response = await SendAsync(HttpMethod.Post, collectionUrl, body, false, ct);
        await EnsureSuccessAsync(response, ct);
        var prepared = await ReadAsync(response, Json.PrepareCaptureRepairResponse, ct);
        if (!Guid.TryParseExact(prepared.RepairId, "N", out _)) throw new IOException("Server returned an invalid repair ID.");
        _operationUrl = collectionUrl + "/" + prepared.RepairId;
        return prepared.RepairId;
    }

    public async Task AddLineAsync(CaptureRepairSourceRequest source, CaptureRepairLineRequest line, CancellationToken ct) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(line, Json.CaptureRepairLineRequest).Length;
        if (_lines.Count > 0 && (_lines.Count == MaxBatchLines || BatchSize(source, bytes) > MaxBatchBytes))
            await FlushAsync(source, ct);
        if (BatchSize(source, bytes) > MaxBatchBytes)
            throw new IOException($"Redacted source line {line.Number} exceeds the 4 MiB recovery request limit; no repair was validated.");
        _lines.Add(line);
        _lineBytes += bytes;
    }

    public async Task<CaptureRepairSourceEndRequest> EndSourceAsync(CaptureRepairSourceRequest source, int lastLine, CancellationToken ct) {
        if (_lines.Count > 0 || _ordinal == 0) await FlushAsync(source, ct);
        var end = new CaptureRepairSourceEndRequest(source, _ordinal - 1, lastLine);
        _ordinal = 0;
        return end;
    }

    public async Task<CaptureRepairResponse> CompleteAsync(CaptureRepairSourceEndRequest[] sources, CancellationToken ct) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CompleteCaptureRepairRequest { Sources = sources }, Json.CompleteCaptureRepairRequest);
        using var response = await SendAsync(HttpMethod.Post, _operationUrl + "/complete", bytes, true, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync(response, Json.CaptureRepairResponse, ct);
    }

    public async Task<CaptureRepairResponse> StatusAsync(CancellationToken ct) {
        using var response = await SendAsync(HttpMethod.Get, _operationUrl!, null, true, ct);
        await EnsureSuccessAsync(response, ct);
        return await ReadAsync(response, Json.CaptureRepairResponse, ct);
    }

    int BatchSize(CaptureRepairSourceRequest source, int nextBytes) =>
        JsonSerializer.SerializeToUtf8Bytes(new UploadCaptureRepairBatchRequest { Source = source, BatchOrdinal = _ordinal }, Json.UploadCaptureRepairBatchRequest).Length
        + _lineBytes + nextBytes + _lines.Count;

    async Task FlushAsync(CaptureRepairSourceRequest source, CancellationToken ct) {
        var body = JsonSerializer.SerializeToUtf8Bytes(new UploadCaptureRepairBatchRequest {
            Source = source, BatchOrdinal = _ordinal, Lines = _lines.ToArray()
        }, Json.UploadCaptureRepairBatchRequest);
        if (body.Length > MaxBatchBytes) throw new IOException("Recovery batch exceeds the 4 MiB request limit.");
        LastBatchDigest = Convert.ToHexStringLower(SHA256.HashData(body));
        using var response = await SendAsync(HttpMethod.Post, _operationUrl + "/batches", body, true, ct);
        await EnsureSuccessAsync(response, ct);
        _ordinal++;
        _lines.Clear();
        _lineBytes = 0;
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, byte[]? body, bool retry, CancellationToken ct) {
        for (var attempt = 0; ; attempt++) {
            try {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null) {
                    request.Content = new ByteArrayContent(body);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                }
                var response = await http.SendAsync(request, ct);
                if (!retry || attempt == 2 || !Transient(response.StatusCode)) return response;
                response.Dispose();
            } catch (HttpRequestException) when (retry && attempt < 2) {
            } catch (OperationCanceledException) when (retry && attempt < 2 && !ct.IsCancellationRequested) {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), time, ct);
        }
    }

    static bool Transient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.IsSuccessStatusCode) return;
        string? reason = null;
        try { reason = (await response.Content.ReadFromJsonAsync(Json.CaptureRepairError, ct))?.Error; }
        catch (JsonException) { }
        // Error codes are protocol tokens; do not echo arbitrary response bodies or transcript text.
        if (reason is not { Length: > 0 and <= 100 } || reason.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) reason = "request_refused";
        throw new IOException($"Capture recovery refused (HTTP {(int)response.StatusCode}): {reason}.");
    }

    static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync(type, ct) ?? throw new IOException("Server returned an empty recovery response.");
}

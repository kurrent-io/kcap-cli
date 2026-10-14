using System.Net.Http.Json;
using System.Text.Json;

namespace Capacitor.Cli.Core.NextWork;

public sealed class NextWorkClient(HttpClient http, string serverUrl) {
    public const int DefaultLimit = 20;

    readonly string _base = serverUrl.TrimEnd('/');

    public async Task<NextWorkRead> ReadAsync(int limit = DefaultLimit, CancellationToken ct = default) {
        var clamped = Math.Clamp(limit, 1, 50);

        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/next-work?limit={clamped}");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            return (int)resp.StatusCode switch {
                401        => NextWorkRead.Of(NextWorkReadKind.SignedOut),
                403 or 404 => NextWorkRead.Of(NextWorkReadKind.Unavailable),
                >= 200 and < 300 when await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.NextWorkFeedDto, ct).ConfigureAwait(false) is { } feed
                    => new(NextWorkReadKind.Ready, feed.Items),
                _ => NextWorkRead.Of(NextWorkReadKind.Unreachable),
            };
        } catch (Exception e) when (IsTransient(e, ct)) {
            return NextWorkRead.Of(NextWorkReadKind.Unreachable);
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}

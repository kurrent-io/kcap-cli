using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Plans;

/// The HTTP channel for `plan-artifacts?chain=true`, the one plans route that carries document
/// bodies. <paramref name="http"/> must already carry the caller's bearer. Degrades rather than
/// throws, except for the caller's own cancellation, which propagates.
public sealed class PlanArtifactsClient(HttpClient http, string serverUrl) {
    readonly string _base = serverUrl.TrimEnd('/');

    public async Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } id) return PlanArtifactsRead.Of(SessionPlansReadKind.Unavailable);

        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/sessions/{Uri.EscapeDataString(id)}/plan-artifacts?chain=true");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            return (int)resp.StatusCode switch {
                401        => PlanArtifactsRead.Of(SessionPlansReadKind.SignedOut),
                403 or 404 => PlanArtifactsRead.Of(SessionPlansReadKind.Unavailable),
                >= 200 and < 300 when await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.PlanArtifactsResponseDto, ct).ConfigureAwait(false) is { } body
                    => new(SessionPlansReadKind.Ready, body),
                _ => PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable),
            };
        } catch (Exception e) when (IsTransient(e, ct)) {
            return PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable);
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}

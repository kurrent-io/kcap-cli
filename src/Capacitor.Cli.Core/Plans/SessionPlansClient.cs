using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Plans;

/// The HTTP channel. <paramref name="http"/> must already carry the caller's bearer. Degrades rather
/// than throws, except for the caller's own cancellation, which propagates: turning a teardown into
/// a failed read would report an outage that never happened.
public sealed class SessionPlansClient(HttpClient http, string serverUrl) {
    readonly string _base = serverUrl.TrimEnd('/');

    public async Task<SessionPlansRead> ReadAsync(string sessionId, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } id) return SessionPlansRead.Of(SessionPlansReadKind.Unavailable);

        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/sessions/{Uri.EscapeDataString(id)}/plans");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            return (int)resp.StatusCode switch {
                401        => SessionPlansRead.Of(SessionPlansReadKind.SignedOut),
                403 or 404 => SessionPlansRead.Of(SessionPlansReadKind.Unavailable),
                >= 200 and < 300 when await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ListSessionPlanDto, ct).ConfigureAwait(false) is { } plans
                    => new(SessionPlansReadKind.Ready, plans),
                _ => SessionPlansRead.Of(SessionPlansReadKind.Unreachable),
            };
        } catch (Exception e) when (IsTransient(e, ct)) {
            return SessionPlansRead.Of(SessionPlansReadKind.Unreachable);
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}

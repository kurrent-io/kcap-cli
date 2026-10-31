using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Pages;

/// The HTTP channel for `GET /api/sessions/{id}/artefacts`: the published pages citing a session that
/// the caller may open. The server applies both checks, the session's visibility and each page's
/// audience, so nothing here filters or builds a link. <paramref name="http"/> must already carry the
/// caller's bearer. Degrades rather than throws, except for the caller's own cancellation.
public sealed class SessionPagesClient(HttpClient http, string serverUrl) {
    readonly string _base = serverUrl.TrimEnd('/');

    public async Task<SessionPagesRead> ReadAsync(string sessionId, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } id) return SessionPagesRead.Of(SessionPlansReadKind.Unavailable);

        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{_base}/api/sessions/{Uri.EscapeDataString(id)}/artefacts");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);

            return (int)resp.StatusCode switch {
                401        => SessionPagesRead.Of(SessionPlansReadKind.SignedOut),
                403 or 404 => SessionPagesRead.Of(SessionPlansReadKind.Unavailable),
                >= 200 and < 300 when await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ArtefactListDto, ct).ConfigureAwait(false)
                        is { Artefacts: { } pages }
                    => new(SessionPlansReadKind.Ready, pages),
                _ => SessionPagesRead.Of(SessionPlansReadKind.Unreachable),
            };
        } catch (Exception e) when (IsTransient(e, ct)) {
            return SessionPagesRead.Of(SessionPlansReadKind.Unreachable);
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException or IOException;
}

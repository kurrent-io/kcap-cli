using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// HTTP adapter behind <see cref="ITitleServerPort"/>: reads a session's title from the
/// summary route and pushes a harness-native one through <c>/hooks/harness-title</c> (falling
/// back to <c>/hooks/set-title</c> against an older server). <see cref="GetTitleAsync"/> throws
/// when the server cannot be asked (auth lapse, transport failure, non-404 error) — the resolve
/// loop must distinguish "the server has no title" from "the server couldn't answer", or an
/// outage would trigger a paid generation for a session the watcher already titled.
/// </summary>
// Qodana's usage search misses the construction in AgentOrchestrator
[UsedImplicitly]
internal sealed partial class TitleServerPort(ICapacitorHttpClient http, string baseUrl) : ITitleServerPort {
    readonly string _baseUrl = baseUrl.TrimEnd('/');

    public async Task<string?> GetTitleAsync(string sessionId, CancellationToken ct) {
        // An id the summary route cannot address (an ACP handshake id, say) has no server-side
        // session to converge with — that is genuine silence, not a failed read.
        if (WorkContextIds.CanonicalSessionId(sessionId) is null) return null;

        var (client, status) = await http.ForHookAsync(ct);

        using (client) {
            if (status is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) {
                throw new InvalidOperationException($"server auth unavailable: {status}");
            }

            var outcome = await new WorkContextClient(client, _baseUrl).GetSessionSummaryAsync(sessionId, ct);

            return outcome.StatusCode switch {
                // A 2xx whose body didn't deserialize is an unreadable answer, not a session
                // without a title — WorkContextClient degrades that to a null body.
                >= 200 and < 300 => outcome.Body is { } body
                    ? body.Title
                    : throw new HttpRequestException("session summary body unreadable"),
                404              => null, // not registered server-side (yet): silence, not failure
                _                => throw new HttpRequestException($"session summary read failed: {outcome.StatusCode}"),
            };
        }
    }

    public async Task<HarnessTitleOutcome> PushTitleAsync(string sessionId, HarnessTitlePost post, CancellationToken ct) {
        // The server files sessions under the canonical key (a GUID as its 32-hex form, an opaque
        // vendor id unchanged) — the raw id must not ride the payload or it would update a
        // different key than the one the summary read used.
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } canonical) return HarnessTitleOutcome.Posted; // nothing to converge with

        var (client, status) = await http.ForHookAsync(ct);

        using (client) {
            if (status is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) return HarnessTitleOutcome.Failed;

            return await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, _baseUrl, canonical, post, ct);
        }
    }

    public async Task<TitleRenameOutcome> RenameAsync(string sessionId, string title, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } canonical) {
            return new TitleRenameOutcome(false, null, SessionRenameReasons.NotFound);
        }

        try {
            var (client, status) = await http.ForHookAsync(ct);

            using (client) {
                if (status is AuthStatus.Expired or AuthStatus.NotAuthenticated or AuthStatus.WrongServer) {
                    return new TitleRenameOutcome(false, null, SessionRenameReasons.Unauthenticated);
                }

                var payload = new JsonObject {
                    ["session_id"] = canonical,
                    ["title"]      = title,
                    ["rename"]     = true,
                };

                using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
                using var resp    = await client.PostAsync(new Uri($"{_baseUrl}/hooks/agent-title"), content, ct);

                switch (resp.StatusCode) {
                    case HttpStatusCode.Forbidden: return new TitleRenameOutcome(false, null, SessionRenameReasons.NotOwned);
                    case HttpStatusCode.NotFound:  return new TitleRenameOutcome(false, null, SessionRenameReasons.NotFound);
                }

                if (!resp.IsSuccessStatusCode) return new TitleRenameOutcome(false, null, SessionRenameReasons.ServerError);

                var reply = JsonSerializer.Deserialize(
                    await resp.Content.ReadAsStringAsync(ct),
                    AgentTitleReplyJsonContext.Default.AgentTitleReplyDto
                );

                if (reply is null) return new TitleRenameOutcome(false, null, SessionRenameReasons.ServerError);

                return reply.Applied
                    ? new TitleRenameOutcome(true, reply.Title, null)
                    : new TitleRenameOutcome(false, reply.Title, SessionRenameReasons.Refused);
            }
        } catch (Exception e) when (e is HttpRequestException or JsonException or IOException or TaskCanceledException && !ct.IsCancellationRequested) {
            return new TitleRenameOutcome(false, null, SessionRenameReasons.ServerError);
        }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(AgentTitleReplyDto))]
    partial class AgentTitleReplyJsonContext : JsonSerializerContext;
}

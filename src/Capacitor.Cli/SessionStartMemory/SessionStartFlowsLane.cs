using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>The flows lane of the composite SessionStart context: the catalogue flows an operator marked
/// <c>offer: proactive</c>, each with when to offer it, and for a hosted agent on a server that takes wake-ups,
/// how to wait. Marker-less, like the guidelines lane. A listing that cannot be fetched in time falls back to the
/// last one the server returned.</summary>
internal sealed class SessionStartFlowsLane(Func<CancellationToken, Task<HttpClient>> client, TimeProvider time, SessionStartFlowsCatalogCache cache) {
    public const int MaxDescribed     = 10;
    public const int MaxFragmentChars = 2048;
    const int WhenToUseCap = 300;
    const int IdCap        = 64;

    public const string Header = "Flows you may offer. When one fits what you just finished, offer it unprompted, and start it only after the user says yes:";
    public const string Footer = "Before starting one, call get_flow_definition(<id>) and follow its guide.";

    public const string WakeUpNudge =
        "## Waiting\n" +
        "This session is a hosted agent. When its next step is due later, with nothing to do until then (CI or the " +
        "reviewers' findings after a push, or a request to check back in a few minutes), call `schedule_wakeup` from " +
        "kcap-flows with what to check, then end your turn. Do not wait with `sleep`, a background command or a " +
        "polling loop: kcap cannot see those, and they end with your process.";

    public async Task<SessionStartMemoryContextResult> FetchAsync(SessionStartMemoryContextRequest request, CancellationToken ct) {
        var wakeUps = request.WakeUpsWanted ? WakeUpsSupportedAsync(request.BaseUrl, ct) : null;
        var catalog = await FetchCatalogAsync(request, ct);

        if (wakeUps is null || !await wakeUps) return catalog;

        // The nudge is marker-less and safe to deliver again, so it rides on a catalogue retry too.
        return catalog.Disposition switch {
            SessionStartMemoryDisposition.Ready when catalog.Fragment is { } fragment => catalog with { Fragment = fragment + "\n\n" + WakeUpNudge },
            SessionStartMemoryDisposition.RetryableFailure                          => catalog with { Fragment = WakeUpNudge },
            _                                                                       => new(SessionStartMemoryDisposition.Ready, WakeUpNudge)
        };
    }

    /// <summary>The routes are registered only while the server's wake-up feature is on, so an unknown route is
    /// the answer. A request without a session id reaches the handler's own 400 when they are there. Anything
    /// else, a failure included, reads as unsupported: pointing an agent at a tool that refuses it is worse than
    /// leaving it to its harness's own scheduling.</summary>
    async Task<bool> WakeUpsSupportedAsync(string baseUrl, CancellationToken ct) {
        try {
            // Its own deadline, shorter than the lane's, so a stalled probe cannot hold back a catalogue already fetched.
            using var deadline = new CancellationTokenSource(ProbeTimeout, time);
            using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var response = await (await client(linked.Token)).GetAsync(
                baseUrl.TrimEnd('/') + Commands.WakeUpTools.Route, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            return response.StatusCode == HttpStatusCode.BadRequest;
        } catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException) {
            return false;
        }
    }

    async Task<SessionStartMemoryContextResult> FetchCatalogAsync(SessionStartMemoryContextRequest request, CancellationToken ct) {
        SessionStartFetchOutcome outcome;
        try {
            outcome = await SessionStartContextFetch.FetchAsync(
                await client(ct), request.BaseUrl.TrimEnd('/') + "/api/flows/definitions", time, ct);
        } catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidDataException) {
            return FromCache(request.BaseUrl) ?? SessionStartMemoryContextResult.Retry;
        }

        if (outcome.Body is null) {
            // A server older than the listing has nothing to offer, now or on a retry; a denied caller
            // must not be offered what it can no longer start.
            if (outcome.Status is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                cache.Delete(request.BaseUrl);
            if (outcome.Status == HttpStatusCode.NotFound) return SessionStartMemoryContextResult.Empty;

            var retry = new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);

            // Only a server that could not answer is stood in for; one that refused the listing, such as a
            // catalogue still catching up, may have withdrawn a cached flow.
            return Unavailable(outcome.Status) ? FromCache(request.BaseUrl) ?? retry : retry;
        }

        JsonNode? root;
        try {
            root = JsonNode.Parse(outcome.Body);
        } catch (JsonException) {
            root = null;
        }

        cache.Write(request.BaseUrl, root);

        return Result(root);
    }

    static bool Unavailable(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    SessionStartMemoryContextResult? FromCache(string baseUrl) => cache.Read(baseUrl) is { } cached ? Result(cached) : null;

    static SessionStartMemoryContextResult Result(JsonNode? root) =>
        BuildFragment(root) is { } fragment
            ? new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment)
            : SessionStartMemoryContextResult.Empty;

    public static string? BuildFragment(JsonNode? root) {
        if (root is not JsonObject { } obj || obj["definitions"] is not JsonArray definitions) return null;

        var offered = definitions.OfType<JsonObject>()
            .Where(d => Str(d, "offer") == "proactive")
            .Select(d => (Id: NextWorkUntrustedText.Render(Str(d, "id"), IdCap), When: NextWorkUntrustedText.Render(Str(d, "when_to_use"), WhenToUseCap)))
            .Where(f => f.Id.Length > 0)
            .ToList();
        if (offered.Count == 0) return null;

        // Described in order until the count or the room runs out; everything after falls back to its id, so the
        // server's id order survives. The overflow line has its own fixed reserve, so the block never passes the cap.
        var room      = MaxFragmentChars - Header.Length - Footer.Length - 2 - OverflowReserve;
        var described = new StringBuilder();
        var idsOnly   = new List<string>();
        var count     = 0;

        foreach (var (id, when) in offered) {
            var line = when.Length > 0 ? $"- {id}: {when}\n" : $"- {id}\n";
            if (idsOnly.Count == 0 && count < MaxDescribed && described.Length + line.Length <= room) {
                described.Append(line);
                count++;
            } else {
                idsOnly.Add(id);
            }
        }

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n').Append(described);
        if (idsOnly.Count > 0) sb.Append(Overflow(idsOnly)).Append('\n');
        sb.Append(Footer);

        return sb.ToString();

        static string Overflow(List<string> ids) {
            var line  = new StringBuilder("Also offerable: ");
            var shown = 0;
            foreach (var id in ids) {
                var piece = shown == 0 ? id : ", " + id;
                if (line.Length + piece.Length > OverflowReserve - OverflowTail.Length - MoreSuffixReserve) break;
                line.Append(piece);
                shown++;
            }

            if (shown < ids.Count) line.Append($" and {ids.Count - shown} more").Append(OverflowTail);

            return line.ToString();
        }

        static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    }

    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    const int    OverflowReserve   = 400;
    const int    MoreSuffixReserve = 16;
    const string OverflowTail      = " (see list_flow_definitions)";
}

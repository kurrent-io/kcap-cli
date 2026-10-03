using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>The flows lane of the composite SessionStart context: the catalogue flows an operator marked
/// <c>offer: proactive</c>, each with when to offer it. Marker-less, like the guidelines lane.</summary>
internal sealed class SessionStartFlowsLane(Func<CancellationToken, Task<HttpClient>> client, TimeProvider time) {
    public const int MaxDescribed     = 10;
    public const int MaxFragmentChars = 2048;
    const int WhenToUseCap = 300;
    const int IdCap        = 64;

    public const string Header = "Flows you may offer (ask first; never start unprompted):";
    public const string Footer = "Before starting one, call get_flow_definition(<id>) and follow its guide.";

    public async Task<SessionStartMemoryContextResult> FetchAsync(SessionStartMemoryContextRequest request, CancellationToken ct) {
        var outcome = await SessionStartContextFetch.FetchAsync(
            await client(ct), request.BaseUrl.TrimEnd('/') + "/api/flows/definitions", time, ct);

        // A server older than the listing has nothing to offer, now or on a retry.
        if (outcome.Status == HttpStatusCode.NotFound) return SessionStartMemoryContextResult.Empty;
        if (outcome.Body is null)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);

        return BuildFragment(JsonNode.Parse(outcome.Body)) is { } fragment
            ? new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment)
            : SessionStartMemoryContextResult.Empty;
    }

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

    const int    OverflowReserve   = 400;
    const int    MoreSuffixReserve = 16;
    const string OverflowTail      = " (see list_flow_definitions)";
}

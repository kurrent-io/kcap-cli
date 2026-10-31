using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>
/// The guidelines lane of the composite SessionStart context provider:
/// fetches the guidelines a session start delivers from
/// <c>GET /api/repositories/{hash}/guidelines/delivery</c> and renders them as a
/// marker-less <c>## Known patterns</c> / <c>## Guidance from past sessions</c>
/// block. The composite prepends the shared memory marker and orders the block
/// after the memory section — this lane returns raw guidelines text only.
///
/// <para>Never falls back to <c>/guidelines</c>: that is the audience-blind dashboard read, and
/// injecting from it hands a team-restricted fact to a caller outside the team. A server too old
/// to deliver gets no guidelines.</para>
///
/// <para>Deliberately does NOT inspect <see cref="SessionStartMemoryContextRequest.Disabled"/>
/// or <c>GuidelinesDisabled</c> — the composite decides which lanes run, so
/// invoking this lane always means "guidelines are enabled for this session".</para>
/// </summary>
internal sealed class SessionStartGuidelinesLane(
    Func<CancellationToken, Task<HttpClient>> client,
    TimeProvider time,
    Action<string>? diagnostic = null) {

    internal const string DeliveryHeader = "X-Kcap-Guideline-Delivery";

    /// <summary>
    /// Fetches and renders the guidelines fragment for an already-resolved
    /// <paramref name="scope"/>. The caller (composite) owns the budget-linked
    /// <paramref name="ct"/>.
    /// </summary>
    public async Task<SessionStartMemoryContextResult> FetchWithScopeAsync(
            SessionStartMemoryScope scope, SessionStartMemoryContextRequest request, CancellationToken ct) {
        // Guidelines are repo-scoped by definition: no repo ⇒ nothing to fetch.
        if (scope.RepoHash is null) return SessionStartMemoryContextResult.Empty;

        var outcome = await SessionStartContextFetch.FetchAsync(
            await client(ct), BuildUrl(request.BaseUrl, scope.RepoHash, request.Vendor, HostPlatform.Normalized), time, ct,
            DeliveryHeader);

        if (outcome.Status == HttpStatusCode.NoContent) return SessionStartMemoryContextResult.Empty;
        if (outcome.Status == HttpStatusCode.BadRequest) {
            diagnostic?.Invoke("SessionStart guidelines endpoint contract mismatch: HTTP 400.");
            return SessionStartMemoryContextResult.Empty;
        }
        if (outcome.Status == HttpStatusCode.NotFound) {
            // Every delivery response carries the header, so a 404 without it is a server too old to deliver.
            if (!outcome.Marked) {
                diagnostic?.Invoke("SessionStart guidelines skipped: the server has no audience-filtered delivery endpoint.");
                return SessionStartMemoryContextResult.Empty;
            }
            // Visibility race: the repo 404s until the caller's own session projects, so a repeat-callback
            // session picks the guidelines up once the read model catches up.
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);
        }
        if (outcome.Body is null)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);

        var response = JsonSerializer.Deserialize(outcome.Body,
            SessionStartMemoryJsonContext.Default.GuidelinesResponse);
        var rows = response?.Guidelines;
        if (rows is null || rows.Length == 0) return SessionStartMemoryContextResult.Empty;

        var fragment = SessionGuidelinesEmitter.BuildFragment(rows);
        return fragment is null
            ? SessionStartMemoryContextResult.Empty
            : new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment);
    }

    internal static string BuildUrl(string baseUrl, string repoHash, string? vendor, string? platform) {
        var url = baseUrl.TrimEnd('/') + "/api/repositories/" + Uri.EscapeDataString(repoHash) + "/guidelines/delivery";
        var query = new List<string>(2);
        if (vendor is { Length: > 0 })   query.Add("vendor=" + Uri.EscapeDataString(vendor));
        if (platform is { Length: > 0 }) query.Add("platform=" + Uri.EscapeDataString(platform));
        return query.Count == 0 ? url : url + "?" + string.Join('&', query);
    }
}

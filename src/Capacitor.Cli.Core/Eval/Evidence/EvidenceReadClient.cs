namespace Capacitor.Cli.Core.Eval.Evidence;

public sealed class EvidenceReadClient(HttpClient http, string baseUrl, string sessionId) {
    public async Task<EvidenceHttpResult> GetAsync(string route, IReadOnlyList<(string Key, string Value)> query, CancellationToken ct) {
        var url = $"{baseUrl.TrimEnd('/')}/api/sessions/{Uri.EscapeDataString(sessionId)}/{route}?{string.Join("&", query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"))}";
        try {
            using var resp = await http.GetAsync(url, ct);
            return new((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        } catch (HttpRequestException e) {
            return new(0, e.Message);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return new(0, "the request timed out");
        }
    }

    /// <summary>Opens the bound token as a cursor page: the server re-runs the root gate and revalidates the scope, and no
    /// evidence is read.</summary>
    public Task<EvidenceHttpResult> ReopenScopeAsync(string token, CancellationToken ct) => GetAsync("evidence-scope", [("cursor", token)], ct);
}

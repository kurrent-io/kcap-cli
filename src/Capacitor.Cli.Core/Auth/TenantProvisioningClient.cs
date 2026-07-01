using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Auth;

// Result of a provision POST: HTTP status + parsed body (202 provisioning /
// 200 active / 400 / 409). Not serialized — no JSON context entry.
public sealed record ProvisionOutcome(int StatusCode, ProvisionResponse? Body);

// Talks to kcap-web /api/signup/* with a WorkOS bearer access token.
public sealed class TenantProvisioningClient(HttpClient http) {
    public async Task<AvailabilityResponse?> CheckAvailabilityAsync(
            string baseUrl, string token, string slug, CancellationToken ct) {
        using var req = Get($"{baseUrl}/api/signup/availability?slug={Uri.EscapeDataString(slug)}", token);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.AvailabilityResponse, ct);
    }

    public async Task<ProvisionOutcome> ProvisionAsync(
            string baseUrl, string token, string orgName, string slug, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            new ProvisionRequest { OrgName = orgName, Slug = slug, Tier = "free" },
            CapacitorJsonContext.Default.ProvisionRequest);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/signup/provision") {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new("Bearer", token);

        using var resp = await http.SendAsync(req, ct);
        ProvisionResponse? body = null;
        try { body = await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.ProvisionResponse, ct); }
        catch (JsonException) { /* empty/non-JSON body — leave null */ }
        return new((int)resp.StatusCode, body);
    }

    public async Task<StatusResponse?> GetStatusAsync(
            string baseUrl, string token, string slug, CancellationToken ct) {
        using var req = Get($"{baseUrl}/api/signup/status?slug={Uri.EscapeDataString(slug)}", token);
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.StatusResponse, ct);
    }

    static HttpRequestMessage Get(string url, string token) {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new("Bearer", token);
        return req;
    }
}

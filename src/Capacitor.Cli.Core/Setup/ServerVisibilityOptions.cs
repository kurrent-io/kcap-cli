using System.Net;
using System.Text.Json;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// The <c>default_visibility</c> stops this tenant can honour. Only the server knows whether an organization stop
/// grants anything beyond a project one, so setup asks rather than offering every stop. Never throws: setup must
/// carry on whatever the server does
/// </summary>
public static class ServerVisibilityOptions {
    public static string Url(string serverUrl) => $"{serverUrl.TrimEnd('/')}/api/users/me/visibility-options";

    public static async Task<VisibilityOptionsRead> ReadAsync(HttpClient http, string serverUrl, CancellationToken ct) {
        try {
            using var response = await http.GetAsync(Url(serverUrl), ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound) return VisibilityOptionsRead.NotServed;
            if (!response.IsSuccessStatusCode) return VisibilityOptionsRead.Failed($"HTTP {(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return Usable(JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.VisibilityOptionsResponse)) is { } offer
                ? new(offer, null)
                : VisibilityOptionsRead.Failed("an answer naming no stop to preselect");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return VisibilityOptionsRead.Failed(ex.Message);
        }
    }

    /// <summary>The stops this build knows, with copy to show, or null when the preselected one is not among them</summary>
    static VisibilityOptionsResponse? Usable(VisibilityOptionsResponse? offer) {
        if (offer is null) return null;

        var known = offer.Options
            .Where(o => o is { Key: { } key, Label.Length: > 0 } && AppConfig.ValidVisibilities.Contains(key))
            .DistinctBy(o => o.Key)
            .ToList();

        return known.Any(o => o.Key == offer.DefaultVisibility) ? offer with { Options = known } : null;
    }
}

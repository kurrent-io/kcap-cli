using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>Whether a server answers at all. Any HTTP status is an answer; only a transport failure or
/// silence is not.</summary>
static class ServerReachability {
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Why <paramref name="baseUrl"/> cannot be reached, or null when it answered.</summary>
    public static async Task<string?> UnreachableReasonAsync(HttpClient http, string baseUrl, TimeProvider time) {
        try {
            using var _ = await http.GetOnceAsync(baseUrl, time, Timeout);

            return null;
        } catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException) {
            return ex.Message;
        } catch (OperationCanceledException) {
            return $"no answer within {Timeout.TotalSeconds:0}s";
        }
    }
}

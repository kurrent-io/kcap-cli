using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public static class ServerSearchHttp {
    public static Func<SearchRequest, CancellationToken, Task<SearchFetch>> Search(
            ICapacitorHttpClient? http, ProfileContext? profiles) => async (request, ct) => {
        var (outcome, body) = await GetAsync(http, profiles,
            ApiRoutes.Search(request.Query, request.Mode.ToWire(), request.Limit),
            RemoteModelsJsonContext.Default.SearchResponseDto, ct).ConfigureAwait(false);
        return new(outcome, body);
    };

    public static Func<string, CancellationToken, Task<SummaryFetch>> Summary(
            ICapacitorHttpClient? http, ProfileContext? profiles) => async (sessionId, ct) => {
        var (outcome, body) = await GetAsync(http, profiles,
            ApiRoutes.SessionSummary(sessionId),
            RemoteModelsJsonContext.Default.ActivitySessionDto, ct).ConfigureAwait(false);
        return new(outcome, body);
    };

    /// Null for a session the caller cannot read, one that does not exist, and any failure.
    public static Func<string, CancellationToken, Task<HandoffContextDto?>> HandoffContext(
            ICapacitorHttpClient? http, ProfileContext? profiles) => async (sessionId, ct) => {
        var (_, body) = await GetAsync(http, profiles,
            ApiRoutes.SessionHandoffContext(sessionId),
            RemoteModelsJsonContext.Default.HandoffContextDto, ct).ConfigureAwait(false);
        return body;
    };

    static async Task<(ServerReadOutcome, T?)> GetAsync<T>(
            ICapacitorHttpClient? http, ProfileContext? profiles, string route,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct) where T : class {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return (ServerReadOutcome.Unauthorized, null);
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return (ServerReadOutcome.Unauthorized, null);
                using var response = await client.GetAsync($"{serverUrl.TrimEnd('/')}/{route}", ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return (ServerReadOutcome.Unauthorized, null);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) return (ServerReadOutcome.Unavailable, null);
                if (!response.IsSuccessStatusCode) return (ServerReadOutcome.Failed, null);
                var body = await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
                return body is null ? (ServerReadOutcome.Failed, null) : (ServerReadOutcome.Ok, body);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return (ServerReadOutcome.Failed, null);
        }
    }
}

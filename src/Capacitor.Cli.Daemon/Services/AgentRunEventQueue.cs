using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Delivers agent-run events to <c>POST /api/agent-runs/{agentId}/events</c> in order, one at a time.
/// A refused event is dropped rather than retried, and a failing one is retried only up to a bound,
/// so one event cannot hold back every event queued behind it.
/// </summary>
internal sealed partial class AgentRunEventQueue(
        DaemonConfig config, TokenStore tokens, TimeProvider time, ILogger logger, HttpClient http)
    : IDisposable {
    /// <summary>About five minutes of backoff: long enough to ride out a server restart.</summary>
    internal const int MaxRetryableResponses = 15;

    static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    readonly Channel<PendingEvent> _channel = Channel.CreateBounded<PendingEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest }
    );

    public void Enqueue(string agentId, object evt) => _channel.Writer.TryWrite(new(agentId, evt));

    public void Complete() => _channel.Writer.TryComplete();

    public async Task RunAsync(CancellationToken ct) {
        try {
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) {
                var eventType = evt.Event.GetType().Name;
                string payload;

                try {
                    payload = Serialize(evt.Event);
                } catch (Exception ex) {
                    LogSerializationFailed(ex, eventType, evt.AgentId);

                    continue;
                }

                if (!await DeliverAsync(evt.AgentId, eventType, payload, ct)) return;
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Posts one event without the queue's retries; throws on any non-success status.</summary>
    public async Task PostDirectAsync(string agentId, object evt, CancellationToken ct) {
        using var response = await PostAsync(agentId, Serialize(evt), ct);
        response.EnsureSuccessStatusCode();
    }

    /// <returns><c>false</c> only when <paramref name="ct"/> was cancelled.</returns>
    async Task<bool> DeliverAsync(string agentId, string eventType, string payload, CancellationToken ct) {
        var retryDelay         = TimeSpan.FromSeconds(1);
        var retryableResponses = 0;

        while (true) {
            try {
                using var response = await PostAsync(agentId, payload, ct);

                if (response.IsSuccessStatusCode) return true;

                var status = (int)response.StatusCode;

                if (!HookSpool.IsRetryable(status)) {
                    LogRefused(eventType, agentId, status);

                    return true;
                }

                if (++retryableResponses >= MaxRetryableResponses) {
                    LogRetriesExhausted(eventType, agentId, status, retryableResponses);

                    return true;
                }

                LogRetrying(eventType, agentId, status, retryDelay.TotalSeconds);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return false;
            } catch (Exception ex) {
                // No response means the server was not reached, which says nothing against this event
                // and would fail every event behind it too, so it does not count toward the bound.
                LogTransportFailed(ex, eventType, agentId, retryDelay.TotalSeconds);
            }

            try {
                await Task.Delay(retryDelay, time, ct);
            } catch (OperationCanceledException) {
                return false;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
        }
    }

    async Task<HttpResponseMessage> PostAsync(string agentId, string payload, CancellationToken ct) {
        var resolution = await tokens.GetValidTokensForServerAsync(config.Profiles.Name, config.ServerUrl, ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.ServerUrl.TrimEnd('/')}/api/agent-runs/{agentId}/events") {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        if (resolution.Tokens?.AccessToken is { } accessToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await http.SendAsync(request, ct);
    }

    static string Serialize(object evt) {
        var data = JsonSerializer.SerializeToNode(evt, evt.GetType(), CapacitorJsonContext.Default)!.AsObject();

        return new JsonObject { ["event_type"] = evt.GetType().Name, ["data"] = data }.ToJsonString();
    }

    public void Dispose() => http.Dispose();

    record PendingEvent(string AgentId, object Event);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to serialize {EventType} for agent {AgentId}, dropping event")]
    partial void LogSerializationFailed(Exception ex, string eventType, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server refused {EventType} for agent {AgentId} with status {StatusCode}, dropping event")]
    partial void LogRefused(string eventType, string agentId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server answered {EventType} for agent {AgentId} with status {StatusCode} {Attempts} times, dropping event")]
    partial void LogRetriesExhausted(string eventType, string agentId, int statusCode, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server answered {EventType} for agent {AgentId} with status {StatusCode}, retrying in {Delay}s")]
    partial void LogRetrying(string eventType, string agentId, int statusCode, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to post {EventType} for agent {AgentId}, retrying in {Delay}s")]
    partial void LogTransportFailed(Exception ex, string eventType, string agentId, double delay);
}

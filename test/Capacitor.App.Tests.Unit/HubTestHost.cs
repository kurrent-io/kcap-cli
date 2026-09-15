using System.Text.Json;
using Capacitor.Remote.Models;
using Eventuous.SignalR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.App.Tests.Unit;

/// A scriptable stand-in for the server's sessions hub: loopback Kestrel on an OS-assigned
/// port, snake_case hub JSON (the real server's policy), no auth. Handlers are static because
/// SignalR constructs a fresh hub instance per invocation.
public sealed class HubTestHost : IAsyncDisposable {
    WebApplication? _app;
    public string Url { get; private set; } = "";

    public static Func<List<DaemonInfo>> DaemonsHandler { get; set; } = () => [];
    public static Func<JsonElement, string> LaunchHandler { get; set; } = _ => "agent-1";
    static int _launchCalls;
    public static int LaunchCalls => _launchCalls;

    public static Func<string, bool> ChatSubscribeHandler { get; set; } = _ => true;
    // Defaults to the chat visibility check: a session hidden from the caller is hidden the same
    // way for both, unless a test scripts the two apart.
    public static Func<string, bool> AccessWatchHandler { get; set; } = sid => ChatSubscribeHandler(sid);
    public static List<string> StopCalls { get; } = [];
    public static List<string> ChatSubscribes { get; } = [];
    public static List<string> ChatUnsubscribes { get; } = [];
    public static List<string> AccessWatches { get; } = [];
    /// What a chat join answers with: the session's queue.
    public static List<QueuedInputItem> ChatSnapshot { get; } = [];

    public static Func<string, bool> StreamHandler { get; set; } = _ => true;
    public static List<(string Stream, ulong? From)> StreamSubscribes { get; } = [];
    public static List<string> StreamUnsubscribes { get; } = [];
    public static Func<string, bool> TerminalHandler { get; set; } = _ => true;
    public static (int Cols, int Rows)? TerminalDims { get; set; }
    public static List<byte[]> TerminalReplay { get; } = [];
    public static List<string> TerminalSubscribes { get; } = [];
    public static List<string> TerminalUnsubscribes { get; } = [];
    public static List<(string AgentId, int Cols, int Rows)> Resizes { get; } = [];
    public static List<string> ResizeReleases { get; } = [];
    public static List<(string AgentId, string Text)> UserInputs { get; } = [];
    public static List<(string AgentId, string Key)> SpecialKeys { get; } = [];

    public static async Task<HubTestHost> StartAsync(bool requireAuth = false) {
        DaemonsHandler = () => [];
        LaunchHandler = _ => "agent-1";
        _launchCalls = 0;
        ChatSubscribeHandler = _ => true;
        AccessWatchHandler = sid => ChatSubscribeHandler(sid);
        StopCalls.Clear();
        ChatSubscribes.Clear();
        ChatUnsubscribes.Clear();
        AccessWatches.Clear();
        ChatSnapshot.Clear();
        StreamHandler = _ => true;
        StreamSubscribes.Clear();
        StreamUnsubscribes.Clear();
        TerminalHandler = _ => true;
        TerminalDims = null;
        TerminalReplay.Clear();
        TerminalSubscribes.Clear();
        TerminalUnsubscribes.Clear();
        Resizes.Clear();
        ResizeReleases.Clear();
        UserInputs.Clear();
        SpecialKeys.Clear();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR().AddJsonProtocol(o =>
            o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        // A short cap so StopAsync forcibly severs a still-open hub connection instead of
        // waiting out the default 30s graceful-drain — a connected-then-closed test would
        // otherwise sit for tens of seconds before the client ever sees the drop.
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromMilliseconds(500));

        var app = builder.Build();
        if (requireAuth)
            app.Use(async (ctx, next) => {
                if (!ctx.Request.Headers.ContainsKey("Authorization")) {
                    ctx.Response.StatusCode = 401;
                    return;
                }
                await next();
            });
        app.MapHub<SessionsHub>("/hubs/sessions");
        await app.StartAsync();

        var host = new HubTestHost { _app = app };
        host.Url = app.Urls.First();
        return host;
    }

    public Task BroadcastAsync(string method, params object?[] args) =>
        _app!.Services.GetRequiredService<IHubContext<SessionsHub>>()
            .Clients.All.SendCoreAsync(method, args);

    /// A stream event, the way the server's gateway delivers one.
    public Task PushStreamEventAsync(StreamEventEnvelope envelope) => BroadcastAsync(SignalRSubscriptionMethods.StreamEvent, envelope);

    public Task StopAsync() => _app!.StopAsync();

    public async ValueTask DisposeAsync() {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    public sealed class SessionsHub : Hub {
        public List<DaemonInfo> GetConnectedDaemons() => DaemonsHandler();

        public string RequestLaunchAgentV2(JsonElement payload) {
            Interlocked.Increment(ref _launchCalls);
            return LaunchHandler(payload);
        }

        public Task RequestStopAgent(string agentId) { StopCalls.Add(agentId); return Task.CompletedTask; }

        public QueuedInputItem[] SubscribeToChat(string sessionId) {
            if (!ChatSubscribeHandler(sessionId)) throw new HubException(WireTokens.SessionNotVisible);
            ChatSubscribes.Add(sessionId);
            return ChatSnapshot.ToArray();
        }

        public Task UnsubscribeFromChat(string sessionId) { ChatUnsubscribes.Add(sessionId); return Task.CompletedTask; }

        public Task RegisterSessionAccessWatch(string sessionId) {
            if (!AccessWatchHandler(sessionId)) throw new HubException(WireTokens.SessionNotVisible);
            AccessWatches.Add(sessionId);
            return Task.CompletedTask;
        }

        public Task SubscribeToStream(string stream, ulong? fromPosition) {
            if (!StreamHandler(stream)) throw new HubException(WireTokens.StreamNotAuthorized);
            StreamSubscribes.Add((stream, fromPosition));
            return Task.CompletedTask;
        }

        public Task UnsubscribeFromStream(string stream) { StreamUnsubscribes.Add(stream); return Task.CompletedTask; }

        // The server's terminal denial is silence, never a throw.
        public async Task SubscribeToTerminal(string agentId) {
            if (!TerminalHandler(agentId)) return;
            TerminalSubscribes.Add(agentId);
            if (TerminalDims is { } dims) await Clients.Caller.SendAsync(HubBroadcasts.TerminalDimensions, agentId, dims.Cols, dims.Rows);
            foreach (var chunk in TerminalReplay)
                await Clients.Caller.SendAsync(HubBroadcasts.TerminalOutput, agentId, Convert.ToBase64String(chunk));
        }

        public Task UnsubscribeFromTerminal(string agentId) { TerminalUnsubscribes.Add(agentId); return Task.CompletedTask; }
        public Task RequestResizeTerminal(string agentId, int cols, int rows) { Resizes.Add((agentId, cols, rows)); return Task.CompletedTask; }
        public Task ReleaseResizeTerminal(string agentId) { ResizeReleases.Add(agentId); return Task.CompletedTask; }
        public Task SendUserInput(string agentId, string text, string[]? attachmentIds) { UserInputs.Add((agentId, text)); return Task.CompletedTask; }
        public Task SendSpecialKey(string agentId, string key) { SpecialKeys.Add((agentId, key)); return Task.CompletedTask; }
    }
}

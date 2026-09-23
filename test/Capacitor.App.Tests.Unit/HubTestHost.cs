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

    /// <param name="admissionDelay">Holds back the server's registration of each connection, to widen
    /// the gap <see cref="BroadcastAsync"/> waits out into one a test can rely on.</param>
    public static async Task<HubTestHost> StartAsync(bool requireAuth = false, TimeSpan? admissionDelay = null) {
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
        builder.Services.AddSingleton<AdmittedConnections>();
        if (admissionDelay is { } delay)
            builder.Services.AddSingleton<HubLifetimeManager<SessionsHub>>(sp =>
                new DelayedAdmission(delay, sp.GetRequiredService<ILogger<DefaultHubLifetimeManager<SessionsHub>>>()));

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

    /// <summary>Sends to every client the hub has admitted, waiting for the first. A client's
    /// StartAsync completes on the handshake response, which the server writes BEFORE it registers
    /// the connection with the lifetime manager, so a send in that gap skips the client silently and
    /// no wait on the receiving side can recover it. The hub's OnConnectedAsync runs after the
    /// registration, so an admitted connection is one <c>Clients.All</c> reaches.</summary>
    public async Task BroadcastAsync(string method, params object?[] args) {
        try {
            await _app!.Services.GetRequiredService<AdmittedConnections>().Any.WaitAsync(TimeSpan.FromSeconds(10));
        } catch (TimeoutException) {
            throw new TimeoutException($"No client was admitted to the hub within 10s; '{method}' would have reached nobody.");
        }

        await _app.Services.GetRequiredService<IHubContext<SessionsHub>>().Clients.All.SendCoreAsync(method, args);
    }

    /// <summary>The connections the hub has admitted, for <see cref="BroadcastAsync"/> to wait on.
    /// <see cref="Any"/> completes while at least one is admitted and is replaced when the last one
    /// leaves, so a broadcast after a reconnect waits for the new connection, not the old.</summary>
    public sealed class AdmittedConnections {
        readonly Lock _lock = new();
        int _count;
        TaskCompletionSource _any = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Any { get { lock (_lock) return _any.Task; } }

        public void Add() {
            lock (_lock) {
                _count++;
                _any.TrySetResult();
            }
        }

        public void Remove() {
            lock (_lock) {
                if (--_count == 0) _any = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// A stream event, the way the server's gateway delivers one.
    public Task PushStreamEventAsync(StreamEventEnvelope envelope) => BroadcastAsync(SignalRSubscriptionMethods.StreamEvent, envelope);

    public Task StopAsync() => _app!.StopAsync();

    public async ValueTask DisposeAsync() {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
    }

    sealed class DelayedAdmission(TimeSpan delay, ILogger<DefaultHubLifetimeManager<SessionsHub>> logger)
        : DefaultHubLifetimeManager<SessionsHub>(logger) {
        public override async Task OnConnectedAsync(HubConnectionContext connection) {
            await Task.Delay(delay);
            await base.OnConnectedAsync(connection);
        }
    }

    public sealed class SessionsHub(AdmittedConnections admitted) : Hub {
        public override Task OnConnectedAsync() {
            admitted.Add();
            return base.OnConnectedAsync();
        }

        public override Task OnDisconnectedAsync(Exception? exception) {
            admitted.Remove();
            return base.OnDisconnectedAsync(exception);
        }

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

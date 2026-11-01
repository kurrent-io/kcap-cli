using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A real hub invokes the daemon the way the server's transport does: <c>RunHeadlessPrompt</c> as a client
/// result, <c>CancelHeadlessPrompt</c> as a one-way send. The cancel reaches the daemon while the run is still pending,
/// so a running prompt never holds the receive loop.</summary>
public class ServerConnectionHeadlessWireTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(15);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class Connected {
        public TaskCompletionSource<string> Id { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class SessionsHub(Connected connected) : Hub {
        public override Task OnConnectedAsync() {
            connected.Id.TrySetResult(Context.ConnectionId);

            return Task.CompletedTask;
        }
    }

    sealed class TestServerConnection(DaemonConfig config)
        : ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance,
                           NullLogger<ServerConnection>.Instance, TimeProvider.System);

    [Test]
    public async Task A_cancel_reaches_the_daemon_while_its_run_is_pending_and_the_answer_comes_back() {
        var connected = new Connected();
        var builder   = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(connected);
        builder.Services.AddSignalR().AddJsonProtocol(o => ServerConnection.ConfigureHubPayload(o.PayloadSerializerOptions));

        await using var app = builder.Build();
        app.MapHub<SessionsHub>("/hubs/sessions");
        await app.StartAsync();

        var config = new DaemonConfig {
            Name       = "test",
            ServerUrl  = app.Urls.Single(),
            ConfigRoot = Config.Root,
            Profiles   = Resolutions.None(Config.Root),
        };
        await using var conn = new TestServerConnection(config);

        var received  = new TaskCompletionSource<RunHeadlessPromptArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.RunHeadlessPromptHandler = async args => {
            received.TrySetResult(args);
            var id = await cancelled.Task;

            return new HeadlessPromptResponse(true, null, null, null, 12, null, FailureKind: "process_failure", Error: id);
        };
        conn.CancelHeadlessPromptHandler = args => {
            cancelled.TrySetResult(args.RequestId);

            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(HangGuard);
        await conn.StartHubAsync(cts.Token);
        var client = app.Services.GetRequiredService<IHubContext<SessionsHub>>().Clients.Client(await connected.Id.Task.WaitAsync(HangGuard));

        var deadline = new DateTimeOffset(2026, 10, 31, 12, 10, 0, TimeSpan.Zero);
        var run = client.InvokeAsync<HeadlessPromptResponse>("RunHeadlessPrompt",
            new RunHeadlessPromptArgs("r1", "claude", "haiku", null, "s", "p", "{}", 2, null, deadline), cts.Token);

        var args = await received.Task.WaitAsync(HangGuard);
        await client.SendAsync("CancelHeadlessPrompt", new CancelHeadlessPromptArgs("r1"), cts.Token);
        var response = await run.WaitAsync(HangGuard);

        await Assert.That(args).IsEqualTo(new RunHeadlessPromptArgs("r1", "claude", "haiku", null, "s", "p", "{}", 2, null, deadline));
        await Assert.That(response).IsEqualTo(new HeadlessPromptResponse(true, null, null, null, 12, null, FailureKind: "process_failure", Error: "r1"));

        await app.StopAsync(cts.Token);
    }
}

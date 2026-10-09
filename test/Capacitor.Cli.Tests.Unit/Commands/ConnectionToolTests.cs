using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ConnectionToolTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    McpSessionsServer Server(string serverUrl, string profile) =>
        new(Config.Root, Resolutions.Of(new Profile { ServerUrl = serverUrl }, profile, serverUrl), AuthFixtures.NewTokenStore(Config.Root),
            new UnusableHttpClient(), NoTelemetry.Startup, new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory), TimeProvider.System);

    static JsonObject Call(string tool) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject() } };

    static JsonObject Payload(string response) {
        var result = JsonNode.Parse(response)!["result"]!;
        return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
    }

    [Test]
    public async Task Lists_get_connection() {
        await Assert.That(McpSessionsServer.BuildToolsList().Select(t => t.Name)).Contains(ConnectionTool.Name);
        await Assert.That(ConnectionTool.Name).IsEqualTo("get_connection");
    }

    [Test]
    public async Task Returns_the_resolved_server_and_profile() {
        var response = await Server("https://work.kcap.ai/", "work").DispatchToolCallAsync(JsonValue.Create(1)!, Call(ConnectionTool.Name));

        var payload = Payload(response);
        await Assert.That(payload["server_url"]!.GetValue<string>()).IsEqualTo("https://work.kcap.ai");
        await Assert.That(payload["profile"]!.GetValue<string>()).IsEqualTo("work");
        await Assert.That(JsonNode.Parse(response)!["result"]!["isError"]).IsNull();
    }

    /// <summary>The HTTP client factory throws on any use, so reaching it fails the call.</summary>
    [Test]
    public async Task Answers_without_any_http_request() {
        var server = Server("https://work.kcap.ai", "work");

        var response = await server.DispatchToolCallAsync(JsonValue.Create(1)!, Call(ConnectionTool.Name));
        await Assert.That(Payload(response)["server_url"]!.GetValue<string>()).IsEqualTo("https://work.kcap.ai");

        var other = await server.DispatchToolCallAsync(JsonValue.Create(2)!, Call("search_sessions"));
        await Assert.That(JsonNode.Parse(other)!["result"]!["isError"]!.GetValue<bool>()).IsTrue();
    }

    sealed class UnusableHttpClient : ICapacitorHttpClient {
        static T Fail<T>() => throw new InvalidOperationException("get_connection must not build an HTTP client.");

        public Task<HttpClient>  ForCommandAsync(CancellationToken ct = default)       => Fail<Task<HttpClient>>();
        public Task<HttpClient>  ForBackgroundAsync(CancellationToken ct = default)    => Fail<Task<HttpClient>>();
        public Task<HttpClient>  ForSessionAsync(CancellationToken ct = default)       => Fail<Task<HttpClient>>();
        public Task<HttpClient>  ForMemoryAsync(CancellationToken ct = default)        => Fail<Task<HttpClient>>();
        public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default)          => Fail<Task<AuthAttempt>>();
        public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default)          => Fail<Task<AuthAttempt>>();
        public Task<AuthAttempt> ForProtectedReadAsync(CancellationToken ct = default) => Fail<Task<AuthAttempt>>();
        public HttpClient        Anonymous()                                           => Fail<HttpClient>();
        public HttpClient        Loopback()                                            => Fail<HttpClient>();
        public HttpClient        Bearer()                                              => Fail<HttpClient>();
    }
}

using System.Net;
using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Bare because two tests below capture the console, which is process-global.
[NotInParallel]
public class PermissionRequestCommandTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    PermissionRequestCommand On(HostedAgent hosted) =>
        new(Config.Root, Resolutions.None(Config.Root), hosted, new RecordingCapacitorHttpClient());

    [Test]
    public async Task A_loopback_bridge_is_the_address_the_hook_posts_to() {
        var ok = On(new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback("http://127.0.0.1:51234/abc")))
                    .TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsTrue();
        await Assert.That(url).IsEqualTo("http://127.0.0.1:51234/abc");
    }

    [Test]
    public async Task No_bridge_falls_back_without_a_word() {
        using var stderr = ConsoleOutput.StartErrorCapture();

        var ok = On(HostedAgent.Terminal).TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
        await Assert.That(stderr.GetCapturedError()).IsEmpty();
    }

    /// <summary>
    /// The fallback to the server route is otherwise indistinguishable from a daemon that named no
    /// bridge, which is what would let a misconfigured variable go unnoticed for a whole session.
    /// </summary>
    [Test]
    public async Task A_refused_bridge_is_reported_before_the_fallback() {
        using var stderr = ConsoleOutput.StartErrorCapture();

        var ok = On(new HostedAgent(null, IsRendered: false, new DaemonBridge.NotLoopback("http://example.com:8080/tok")))
                    .TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
        await Assert.That(stderr.GetCapturedError()).Contains("http://example.com:8080/tok");
    }

    [Test]
    public async Task Bridge_payload_adds_agent_id_and_cwd_and_leaves_the_server_shape_alone() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","tool_name":"Bash","tool_input":{"command":"ls"},"permission_suggestions":null,"cwd":"/repo","transcript_path":"/t"}""")!;
        var bridge = PermissionRequestCommand.BuildBridgePayload(node, "abc", "agent-1");
        await Assert.That(bridge["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(bridge["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(bridge["tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(bridge["transcript_path"]).IsNull();

        var withoutAgent = PermissionRequestCommand.BuildBridgePayload(node, "abc", null);
        await Assert.That(withoutAgent["agent_id"]).IsNull();
        await Assert.That(withoutAgent["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
    }

    sealed class Accepting : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("""{"behavior":"allow"}""", Encoding.UTF8, "application/json")
            });
    }

    /// <summary>
    /// The bridge post carries the raw tool input to an address the daemon minted, so it must draw the
    /// lane that ignores an ambient proxy — the agent's own environment supplies one often enough, and
    /// a proxied hop would take the payload off the machine.
    /// </summary>
    [Test]
    public async Task The_bridge_post_draws_the_loopback_lane() {
        using var handler = new Accepting();
        var       http     = new RecordingCapacitorHttpClient(handler);

        var command = new PermissionRequestCommand(
            Config.Root, Resolutions.None(Config.Root),
            // The bridge is the rendered agent's route; a terminal one records the event and never posts.
            new HostedAgent(null, IsRendered: true, new DaemonBridge.Loopback("http://127.0.0.1:51234/bridge")), http);

        await using var stdout = new StringWriter();

        var exit = await command.Handle(
            """{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"ls"}}""",
            selfHealWatcher: false, stdout);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "Loopback" });
    }

    sealed class Counting : HttpMessageHandler {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref Requests);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// Recording is best-effort and the credential is gone, so the POST could only earn a 401 — and
    /// this hook is holding up the approval prompt while it waits for one.
    /// </summary>
    [Test]
    public async Task A_lapsed_credential_records_nothing() {
        using var handler = new Counting();
        var       http     = new RecordingCapacitorHttpClient(handler, AuthStatus.NotAuthenticated);

        var command = new PermissionRequestCommand(
            Config.Root, Resolutions.At("https://example.test", Config.Root), HostedAgent.Terminal, http);

        var exit = await command.Handle(
            """{"session_id":"s1","tool_name":"Bash","tool_input":{"command":"ls"}}""",
            selfHealWatcher: false);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(handler.Requests).IsEqualTo(0);
        // The waiting lane, not the hook one: this leg is answered by a human, so it has to outlive
        // a token that lapses mid-wait. Pinning the lane is what keeps that from being withdrawn.
        await Assert.That(http.Lanes).IsEquivalentTo(new[] { "ForWaitAsync" });
    }
}

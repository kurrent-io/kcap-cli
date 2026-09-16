using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The tool-settled relay: a PTY vendor's hook tells the daemon that a tool ran, a subagent
/// stopped or the turn ended, and the bridge hands the attributed agent's notice to the
/// orchestrator so a prompt answered in the terminal can be retired.
public class LocalPermissionBridgeToolSettledTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    sealed class Harness : IAsyncDisposable {
        public LocalPermissionBridge Bridge { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
        public List<(string AgentId, ToolSettledNotice Notice)> Seen { get; } = [];

        public Harness(string? attributeTo = "agent-1") {
            Bridge = new LocalPermissionBridge(new FakeServerConnection(respond: null), NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance) {
                AttributeHandler   = attributeTo is null ? _ => null : _ => new AttributedAgent(attributeTo),
                ToolSettledHandler = (id, notice) => Seen.Add((id, notice)),
            };
        }

        public Task StartAsync() => Bridge.StartAsync(CancellationToken.None);

        public Task<HttpResponseMessage> PostAsync(object body, string vendor = "claude", string? token = null) {
            var baseUrl = token is null ? Bridge.BaseUrl! : $"http://127.0.0.1:{new Uri(Bridge.BaseUrl!).Port}/{token}";
            return Client.PostAsync($"{baseUrl}/{vendor}/tool-settled", JsonContent.Create(body));
        }

        public async ValueTask DisposeAsync() { await Bridge.DisposeAsync(); Client.Dispose(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task A_settled_tool_reaches_the_attributed_agent_with_its_id() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", cwd = "/repo", tool_use_id = "toolu_1" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", new ToolSettledNotice("toolu_1", null)));
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task A_stopped_subagent_reaches_the_agent_with_the_subagent_id() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", new ToolSettledNotice(null, "sub-1")));
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task A_finished_turn_reaches_the_agent_with_no_scope() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1" }, vendor: "codex");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", new ToolSettledNotice(null, null)));
    }

    /// A notice the ladder cannot place is not an error the hook can act on, so it is acknowledged
    /// and dropped rather than refused.
    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task An_unattributed_notice_is_acknowledged_and_dropped() {
        await using var h = new Harness(attributeTo: null);
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, tool_use_id = "toolu_1" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task A_notice_without_a_session_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { agent_id = "agent-1", tool_use_id = "toolu_1" });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    /// An id past the wire cap can match no pending request, and treating it as absent would
    /// widen the notice to the whole turn. Same for an empty one.
    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task A_malformed_scope_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var overCap  = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", tool_use_id = new string('x', 129) });
        var empty    = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", tool_use_id = "" });
        var subagent = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "" });

        await Assert.That(overCap.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(empty.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(subagent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task A_body_that_is_not_an_object_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var array  = await h.PostAsync(new[] { 1 });
        var scalar = await h.PostAsync(true);

        await Assert.That(array.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(scalar.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    /// Only the PTY vendors relay through hooks; every other runtime settles its own prompts.
    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task A_vendor_that_settles_its_own_prompts_has_no_route() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", tool_use_id = "toolu_1" }, vendor: "cursor");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeToolSettledTests))]
    public async Task An_unknown_token_has_no_route() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", tool_use_id = "toolu_1" }, token: "nope");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }
}

using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpHandoffServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Previous = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Current  = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    McpHandoffServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), NoTelemetry.Startup, TimeProvider.System);

    async Task<string> Call(string argsJson, HttpMessageHandler handler, string? current = Current) {
        using var client = new HttpClient(handler);
        var request = new JsonObject {
            ["params"] = new JsonObject { ["name"] = "continue_session", ["arguments"] = JsonNode.Parse(argsJson) }
        };
        return await Server().HandleToolCallAsync(JsonValue.Create(1)!, request, client, "http://x", current);
    }

    static string Text(string response) => JsonNode.Parse(response)!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    static bool IsError(string response) => JsonNode.Parse(response)!["result"]!["isError"]?.GetValue<bool>() == true;

    [Test]
    public async Task Advertises_only_continue_session() {
        var tools = McpHandoffServer.BuildToolsList();
        await Assert.That(tools.Select(t => t.Name)).IsEquivalentTo(new[] { "continue_session" });
        await Assert.That(tools[0].InputSchema.Required).IsEquivalentTo(new[] { "session_id" });
        await Assert.That(tools[0].InputSchema.Properties.Keys).Contains("force");
    }

    [Test]
    public async Task Is_registered_auto_approved_and_kept_away_from_reviewers() {
        var server = KcapMcpServers.All.Single(s => s.Name == "kcap-handoff");
        await Assert.That(server.AutoApprove).IsTrue();
        await Assert.That(server.Args).IsEquivalentTo(new[] { "mcp", "handoff" }, CollectionOrdering.Matching);
        await Assert.That(KcapMcpRegistry.ReviewFlowAutoApprovableServers.Contains("kcap-handoff")).IsFalse();
    }

    [Test]
    public async Task Returns_the_outcome_as_json() {
        var handler = new Answers(req => req.RequestUri!.AbsolutePath switch {
            $"/api/sessions/{Previous}/summary" => (200, """{"status":"ended"}"""),
            _                                   => (200, "[]"),
        });

        var response = await Call($$"""{"session_id":"{{Previous}}"}""", handler);

        await Assert.That(IsError(response)).IsFalse();
        await Assert.That(JsonNode.Parse(Text(response))!["continued_from"]!.GetValue<string>()).IsEqualTo(Previous);
    }

    [Test]
    public async Task A_refusal_is_a_tool_error_carrying_the_reason() {
        var response = await Call($$"""{"session_id":"{{Current}}"}""", new Answers(_ => (200, "{}")));

        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(Text(response)).Contains("cannot continue itself");
    }

    [Test]
    public async Task No_current_session_is_a_tool_error() {
        var response = await Call($$"""{"session_id":"{{Previous}}"}""", new Answers(_ => (200, "{}")), current: null);

        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(Text(response)).Contains("CLAUDE_CODE_SESSION_ID");
    }

    [Test]
    public async Task Force_must_be_a_boolean() {
        var response = await Call($$"""{"session_id":"{{Previous}}","force":"yes"}""", new Answers(_ => (200, "{}")));

        await Assert.That(IsError(response)).IsTrue();
    }

    sealed class Answers(Func<HttpRequestMessage, (int Status, string Body)> answer) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var (status, body) = answer(request);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        }
    }
}

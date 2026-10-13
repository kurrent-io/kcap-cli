using System.Net;
using System.Text;
using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Tests.Unit.Harness.GrokBot;

/// <summary>Pins which <c>listAgents</c> fields count as a change. <c>lastEntry</c> previews how the last
/// message opens and <c>localActivityAt</c> is the chat's creation time, so a fingerprint built on them stays
/// the same while the chat grows.</summary>
public class GrokBotGatewayTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Canned(string body) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    async Task<GrokBotAgent> ListOne(string agentJson) {
        var descriptor = Tmp.CreateFile("gateway.json", """{"scheme":"http","host":"0.0.0.0","port":1340,"token":"t"}""");
        using var http = new HttpClient(new Canned($"[{agentJson}]"));
        return (await new GrokBotGateway(descriptor, http).ListAgentsAsync(CancellationToken.None)).Single();
    }

    const string Base = "\"id\":\"g1\",\"name\":\"weather\",\"isGroup\":true,\"isRunningTurn\":false,\"lastEntry\":{\"kind\":\"text\",\"text\":\"```\"},\"localActivityAt\":1";

    [Test]
    public async Task A_new_entry_changes_the_fingerprint() {
        var before = await ListOne($$"""{{{Base}},"newestEntryId":"t3s0","updatedAt":100}""");
        var after  = await ListOne($$"""{{{Base}},"newestEntryId":"t4u","updatedAt":200}""");

        await Assert.That(after.Fingerprint).IsNotEqualTo(before.Fingerprint);
    }

    [Test]
    public async Task A_finished_turn_changes_the_fingerprint() {
        var running = await ListOne($$"""{"id":"g1","name":"weather","isRunningTurn":true,"newestEntryId":"t4s0"}""");
        var idle    = await ListOne($$"""{"id":"g1","name":"weather","isRunningTurn":false,"newestEntryId":"t4s0"}""");

        await Assert.That(idle.Fingerprint).IsNotEqualTo(running.Fingerprint);
    }

    [Test]
    public async Task Preview_and_creation_time_alone_do_not_count_as_a_change() {
        var a = await ListOne($$"""{"id":"g1","name":"w","newestEntryId":"t4u","lastEntry":{"kind":"text","text":"```"},"localActivityAt":1}""");
        var b = await ListOne($$"""{"id":"g1","name":"w","newestEntryId":"t4u","lastEntry":{"kind":"text","text":"Sure"},"localActivityAt":2}""");

        await Assert.That(b.Fingerprint).IsEqualTo(a.Fingerprint);
    }

    [Test]
    public async Task Groups_are_listed() {
        var group = await ListOne($$"""{{{Base}}}""");

        await Assert.That(group.Id).IsEqualTo("g1");
    }
}

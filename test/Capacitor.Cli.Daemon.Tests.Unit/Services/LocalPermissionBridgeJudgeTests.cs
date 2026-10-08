using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The judge at the hosted permission seam, end to end through the bridge: its allow or deny answers
/// the hook with no card raised, and a judge that does not decide leaves the request on the human
/// lane with the consultation recorded.
public class LocalPermissionBridgeJudgeTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    const string Rules = "version: 1\njudge:\n  mode: unmatched\n";

    static PolicySnapshot Governed => new("snap-1", [
        new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", Rules,
            PolicyDocumentBinder.Bind(Rules, PolicyScope.Repo))], false, []);

    sealed class Harness : IAsyncDisposable {
        public WireMockServer         Judge  { get; } = WireMockServer.Start();
        public PolicyServerConnection Server { get; } = new();
        public PermissionPromptBroker Broker { get; } = new();
        public TempDir                Tmp    { get; } = new();
        public HttpClient             Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
        public LocalPermissionBridge  Bridge { get; }

        public Harness() {
            var gateway = new PolicyJudgeGateway(
                () => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired)), Judge.Url, TimeProvider.System);
            Bridge = new LocalPermissionBridge(Server, NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance,
                TimeProvider.System, Broker, new PermissionDecisionLog(Tmp.Path, NullLogger.Instance), gateway,
                new ConfigRoot(Tmp.CreateDir("config").Path)) {
                AttributeHandler = _ => new AttributedAgent("agent-1", Governed),
                // Far above production's: a cold first consultation under a loaded suite can outlive
                // it and park the request on a card nothing settles.
                JudgeBudget = TimeSpan.FromSeconds(20),
            };
        }

        public void Answer(string outcome) =>
            Judge.Given(Request.Create().WithPath(PolicyJudgeClient.Route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                    $$"""{"outcome":"{{outcome}}","consultation_id":"c-1","artifact_id":"a-1","lineage":"none","cache":"miss"}"""));

        public async Task StartAsync() => await Bridge.StartAsync(CancellationToken.None);

        string? _transcript;

        string Transcript() => _transcript ??= Tmp.CreateFile("t.jsonl",
            """{"type":"user","uuid":"11111111-1111-1111-1111-111111111111","promptId":"p1","message":{"role":"user","content":"go"}}""" + "\n");

        public Task<HttpResponseMessage> PostAsync(string command, string toolUseId = "toolu_1") =>
            Client.PostAsync($"{Bridge.BaseUrl}/claude/permission-request",
                JsonContent.Create(new {
                    session_id = Session, tool_name = "Bash", tool_input = new { command },
                    agent_id = "agent-1", cwd = "/wt", tool_use_id = toolUseId, transcript_path = Transcript(),
                }));

        public JsonElement JudgeRequestFor(string toolUseId) => JudgeRequests().Single(r =>
            r.GetProperty("turns").GetProperty("tool_use_id").GetString() == toolUseId);

        IEnumerable<JsonElement> JudgeRequests() =>
            Judge.LogEntries.Select(e => JsonDocument.Parse(e.RequestMessage.Body!).RootElement);

        public static async Task<string> BehaviorOf(HttpResponseMessage response) {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString()!;
        }

        public async Task<PermissionPendingDto> WaitPendingAsync() {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Broker.PendingSnapshot().Count == 0) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for a pending request");
                await Task.Delay(10);
            }
            return Broker.PendingSnapshot().Single();
        }

        public async ValueTask DisposeAsync() {
            await Bridge.DisposeAsync();
            Client.Dispose();
            Judge.Stop();
            Tmp.Dispose();
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeJudgeTests))]
    [Arguments("allow")]
    [Arguments("deny")]
    public async Task The_judges_decision_answers_the_hook_without_raising_a_card(string outcome) {
        await using var h = new Harness();
        h.Answer(outcome);
        await h.StartAsync();

        var response = await h.PostAsync("git push");

        await Assert.That(await Harness.BehaviorOf(response)).IsEqualTo(outcome);
        await Assert.That(h.Broker.PendingSnapshot().Count).IsEqualTo(0);
        await Assert.That(h.Server.BeginCount).IsEqualTo(0);

        var evt = h.Server.PolicyEvents().Single();
        await Assert.That(evt.EffectiveOutcome).IsEqualTo(outcome);
        await Assert.That(evt.Judge!.ConsultationId).IsEqualTo("c-1");

        var request = JsonDocument.Parse(h.Judge.LogEntries.Single().RequestMessage.Body!).RootElement;
        await Assert.That(request.GetProperty("agent_id").GetString()).IsEqualTo("agent-1");
        await Assert.That(request.GetProperty("turns").GetProperty("tool_use_id").GetString()).IsEqualTo("toolu_1");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeJudgeTests))]
    public async Task An_undecided_judge_leaves_the_request_on_the_human_lane() {
        await using var h = new Harness();
        h.Answer("uncertain");
        await h.StartAsync();

        var response = h.PostAsync("git push");
        var pending  = await h.WaitPendingAsync();
        h.Broker.TrySettle(pending.RequestId, new PermissionDecision("allow", null, null), "allow", "app");

        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
        var evt = h.Server.PolicyEvents().Single();
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("pass_through");
        await Assert.That(evt.FailureClass).IsEqualTo(PolicyJudgeResult.Uncertain);
    }

    /// <summary>A human's no on the card reaches Claude as a hook deny its transcript does not mark,
    /// so only the bridge can tell the judge about it on the retry.</summary>
    [Test, NotInParallel(nameof(LocalPermissionBridgeJudgeTests))]
    public async Task A_card_deny_is_declared_to_the_next_consultation_in_the_session() {
        await using var h = new Harness();
        h.Answer("uncertain");
        await h.StartAsync();

        var first = h.PostAsync("git push", "toolu_1");
        h.Broker.TrySettle((await h.WaitPendingAsync()).RequestId, new PermissionDecision("deny", null, null), "deny", "app");
        await Assert.That(await Harness.BehaviorOf(await first)).IsEqualTo("deny");

        var retry = h.PostAsync("git push", "toolu_2");
        h.Broker.TrySettle((await h.WaitPendingAsync()).RequestId, new PermissionDecision("allow", null, null), "allow", "app");
        await retry;

        var before = h.JudgeRequestFor("toolu_1").GetProperty("refusals");
        await Assert.That(before.GetProperty("entries").GetArrayLength()).IsEqualTo(0);

        var after   = h.JudgeRequestFor("toolu_2").GetProperty("refusals");
        var refused = after.GetProperty("entries").EnumerateArray().Single();
        await Assert.That(after.GetProperty("complete").GetBoolean()).IsTrue();
        await Assert.That(refused.GetProperty("tool_use_id").GetString()).IsEqualTo("toolu_1");
        await Assert.That(refused.GetProperty("tool").GetString()).IsEqualTo("Bash");
        await Assert.That(refused.GetProperty("target").GetString()).IsEqualTo("git push");
    }
}

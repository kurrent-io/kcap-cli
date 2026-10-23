using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>The stdio half of a stream-json Claude agent: user input and interrupts go out as JSON
/// lines, and a <c>can_use_tool</c> request is decided through the bridge and answered on stdin.</summary>
public class ClaudeStreamJsonHostedAgentRuntimeTests {
    const string Session = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";

    sealed class Rig : IAsyncDisposable {
        public FakeStreamJsonProcess                 Process   { get; } = new();
        public List<JsonObject>                      Asked     { get; } = [];
        public TaskCompletionSource<PermissionDecision> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CancellationToken>  AskToken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ClaudeStreamJsonHostedAgentRuntime    Runtime   { get; }

        public Rig() =>
            Runtime = new ClaudeStreamJsonHostedAgentRuntime(
                Process,
                (request, ct) => {
                    lock (Asked) Asked.Add(request);
                    AskToken.TrySetResult(ct);
                    return Decision.Task.WaitAsync(ct);
                },
                session => $"/projects/{session}.jsonl",
                "agent-1", "/wt", NullLogger.Instance, TimeProvider.System);

        public void Init() => Process.Push($$"""{"type":"system","subtype":"init","session_id":"{{Session}}"}""");

        public void Ask(string requestId, string toolName, string input) => Process.Push(
            $$$"""{"type":"control_request","request_id":"{{{requestId}}}","request":{"subtype":"can_use_tool","tool_name":"{{{toolName}}}","input":{{{input}}},"permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu_01X"}}""");

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Test]
    public async Task A_permission_request_is_decided_through_the_bridge_in_the_hooks_shape() {
        await using var rig = new Rig();
        rig.Init();
        rig.Ask("req-1", "Bash", """{"command":"touch x"}""");
        await rig.AskToken.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);

        var asked = rig.Asked.Single();
        await Assert.That(asked["session_id"]!.GetValue<string>()).IsEqualTo(Session);
        await Assert.That(asked["tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(asked["tool_input"]!["command"]!.GetValue<string>()).IsEqualTo("touch x");
        await Assert.That(asked["permission_suggestions"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(asked["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(asked["cwd"]!.GetValue<string>()).IsEqualTo("/wt");
        await Assert.That(asked["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_01X");
        await Assert.That(asked["transcript_path"]!.GetValue<string>()).IsEqualTo($"/projects/{Session}.jsonl");
    }

    /// <summary>An allow without an edited input hands the call's own input back: Claude validates
    /// <c>updatedInput</c> as the whole tool input.</summary>
    [Test]
    public async Task An_allow_answers_with_the_input_and_a_deny_with_a_message() {
        await using var rig = new Rig();
        rig.Init();
        rig.Ask("req-1", "Bash", """{"command":"touch x"}""");
        rig.Decision.SetResult(new PermissionDecision("allow", null, null));

        var response = Parse(await rig.Process.NextWriteAsync()).GetProperty("response");
        await Assert.That(response.GetProperty("request_id").GetString()).IsEqualTo("req-1");
        await Assert.That(response.GetProperty("response").GetProperty("behavior").GetString()).IsEqualTo("allow");
        await Assert.That(response.GetProperty("response").GetProperty("updatedInput").GetProperty("command").GetString()).IsEqualTo("touch x");

        var deny = ClaudeStreamJsonHostedAgentRuntime.PermissionResponse("req-2", PermissionSettlements.DenyDecision, null);
        await Assert.That(deny["response"]!["response"]!["behavior"]!.GetValue<string>()).IsEqualTo("deny");
        await Assert.That(deny["response"]!["response"]!["message"]).IsNotNull();
    }

    /// <summary>A question's answers come back from the server as the whole updated input.</summary>
    [Test]
    public async Task A_decisions_updated_input_and_rules_replace_the_calls_own() {
        var answered = Parse("""{"questions":[{"question":"Colour?"}],"answers":{"Colour?":"Purple"}}""");
        var rules    = Parse("""[{"type":"setMode","mode":"acceptEdits","destination":"session"}]""");

        var response = ClaudeStreamJsonHostedAgentRuntime.PermissionResponse(
            "req-1", new PermissionDecision("allow", rules, answered), Parse("""{"questions":[]}"""))["response"]!["response"]!;

        await Assert.That(response["updatedInput"]!["answers"]!["Colour?"]!.GetValue<string>()).IsEqualTo("Purple");
        await Assert.That(response["updatedPermissions"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_withdrawn_request_cancels_its_decision_and_is_not_answered() {
        await using var rig = new Rig();
        rig.Init();
        rig.Ask("req-1", "Bash", """{"command":"touch x"}""");
        var token = await rig.AskToken.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System);

        rig.Process.Push("""{"type":"control_cancel_request","request_id":"req-1"}""");

        await Assert.That(async () => await Task.Delay(Timeout.Infinite, token)).Throws<TaskCanceledException>();
        await Task.Delay(50);
        await Assert.That(rig.Process.HasPendingWrite).IsFalse();
    }

    /// <summary>Nothing can be attributed before the session is known, so nothing is asked.</summary>
    [Test]
    public async Task A_request_before_the_session_is_known_is_denied_without_asking() {
        await using var rig = new Rig();
        rig.Ask("req-1", "Bash", """{"command":"touch x"}""");

        var response = Parse(await rig.Process.NextWriteAsync()).GetProperty("response").GetProperty("response");

        await Assert.That(response.GetProperty("behavior").GetString()).IsEqualTo("deny");
        await Assert.That(rig.Asked.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Input_is_a_user_message_and_escape_is_an_interrupt() {
        await using var rig = new Rig();

        await rig.Runtime.SendUserInputAsync("fix the login bug");
        var user = Parse(await rig.Process.NextWriteAsync());
        await Assert.That(user.GetProperty("type").GetString()).IsEqualTo("user");
        await Assert.That(user.GetProperty("message").GetProperty("content").GetString()).IsEqualTo("fix the login bug");

        await rig.Runtime.SendSpecialKeyAsync("Escape");
        var interrupt = Parse(await rig.Process.NextWriteAsync());
        await Assert.That(interrupt.GetProperty("request").GetProperty("subtype").GetString()).IsEqualTo("interrupt");
    }

    [Test]
    public async Task Input_is_refused_once_the_agent_has_exited() {
        await using var rig = new Rig();
        rig.Process.EndOfStream();
        await foreach (var _ in rig.Runtime.ReadOutputAsync()) { }

        await Assert.That(() => rig.Runtime.SendUserInputAsync("late")).Throws<InputNotAdmittedException>();
    }
}

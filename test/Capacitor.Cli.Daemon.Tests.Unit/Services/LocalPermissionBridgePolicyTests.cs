using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The launched agent's own policy, evaluated at the hosted permission seam: an allow or a deny
/// answers the hook with no card raised, and everything else reaches the human lane unchanged.
public class LocalPermissionBridgePolicyTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    const string Rules = """
        version: 1
        rules:
          - match: { kind: shell, command: "git push --force*" }
            outcome: deny
          - match: { kind: shell, command: "git status *" }
            outcome: allow
          - match: { kind: shell, command: "gh pr merge" }
            outcome: ask
        """;

    // Evaluation reads the bound document, never the Content string, so the full rules text has to
    // reach Bind — a snapshot carrying only a header would evaluate as an empty rule set.
    static PolicySnapshot Governed => new("snap-1", [
        new PolicyScopeDocument(PolicyScope.Repo, "/wt/.kcap/approvals.yaml", Rules,
            PolicyDocumentBinder.Bind(Rules, PolicyScope.Repo))], false, []);

    sealed class Harness : IAsyncDisposable {
        public PolicyServerConnection Server { get; } = new();
        public PermissionPromptBroker Broker { get; } = new();
        public TempDir                Tmp    { get; } = new();
        public HttpClient             Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
        public PermissionDecisionLog  Log    { get; }
        public LocalPermissionBridge  Bridge { get; }

        public Harness(PolicySnapshot? snapshot, string? approvalsReviewer = null) {
            Log    = new PermissionDecisionLog(Tmp.Path, NullLogger.Instance);
            Bridge = new LocalPermissionBridge(Server, NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, TimeProvider.System, Broker, Log) {
                AttributeHandler = _ => new AttributedAgent("agent-1", snapshot, approvalsReviewer),
            };
        }

        public async Task StartAsync() => await Bridge.StartAsync(CancellationToken.None);

        /// Posts a hook payload for one Bash command; the returned task completes when the hook is
        /// answered — immediately for a policy decision, on settlement for a parked request.
        public Task<HttpResponseMessage> PostAsync(string command, string vendor = "claude") =>
            Client.PostAsync($"{Bridge.BaseUrl}/{vendor}/permission-request",
                JsonContent.Create(new {
                    session_id = Session, tool_name = "Bash", tool_input = new { command },
                    agent_id = "agent-1", cwd = "/wt",
                }));

        public static async Task<string> BehaviorOf(HttpResponseMessage response) {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString()!;
        }

        public string[] LogLines() {
            var path = Tmp.PathTo("permission-decisions.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }

        public async Task<PermissionPendingDto> WaitPendingAsync() {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Broker.PendingSnapshot().Count == 0) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for a pending request");
                await Task.Delay(10);
            }
            return Broker.PendingSnapshot().Single();
        }

        public async ValueTask DisposeAsync() { await Bridge.DisposeAsync(); Client.Dispose(); Tmp.Dispose(); }
    }

    static PermissionDecision Allow => new("allow", null, null);

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task Policy_deny_answers_the_hook_without_raising_a_card() {
        await using var h = new Harness(Governed);
        await h.StartAsync();
        var (_, reader) = h.Broker.Subscribe();

        var response = await h.PostAsync("git push --force");

        await Assert.That(await Harness.BehaviorOf(response)).IsEqualTo("deny");
        await Assert.That(h.Broker.PendingSnapshot().Count).IsEqualTo(0);
        await Assert.That(reader.TryRead(out _)).IsFalse().Because("a policy-answered call never registers with the broker");
        await Assert.That(h.Server.BeginCount).IsEqualTo(0);

        var line = h.LogLines().Single();
        await Assert.That(line).Contains("\"outcome\":\"deny\"");
        await Assert.That(line).Contains("\"source\":\"policy\"");

        var evt = h.Server.PolicyEvents().Single();
        await Assert.That(evt.Seam).IsEqualTo(PolicySeams.HostedClaudePermission);
        await Assert.That(evt.Vendor).IsEqualTo("claude");
        await Assert.That(evt.SessionId).IsEqualTo(Session);
        await Assert.That(evt.AgentId).IsEqualTo("agent-1");
        await Assert.That(evt.SnapshotId).IsEqualTo("snap-1");
        await Assert.That(evt.EvaluationMode).IsEqualTo("full");
        await Assert.That(evt.RequestedOutcome).IsEqualTo("deny");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("deny");
        await Assert.That(evt.CorrelationId).IsNull();
        await Assert.That(evt.CorrelationAmbiguous).IsFalse();
        await Assert.That(evt.MatchedRules.Single().Outcome).IsEqualTo("deny");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task Policy_allow_answers_the_hook_without_raising_a_card() {
        await using var h = new Harness(Governed);
        await h.StartAsync();
        var (_, reader) = h.Broker.Subscribe();

        var response = await h.PostAsync("git status");

        await Assert.That(await Harness.BehaviorOf(response)).IsEqualTo("allow");
        await Assert.That(h.Broker.PendingSnapshot().Count).IsEqualTo(0);
        await Assert.That(reader.TryRead(out _)).IsFalse().Because("a policy-answered call never registers with the broker");
        await Assert.That(h.Server.BeginCount).IsEqualTo(0);
        await Assert.That(h.LogLines().Single()).Contains("\"source\":\"policy\"");

        var evt = h.Server.PolicyEvents().Single();
        await Assert.That(evt.RequestedOutcome).IsEqualTo("allow");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("allow");
        await Assert.That(evt.MatchedRules.Single().Outcome).IsEqualTo("allow");

        // The allowed call is still owed its notice, which must not retire an identical prompt.
        using var input = JsonDocument.Parse("""{"command":"git status"}""");
        var waiting = h.Broker.Register(new PermissionPendingDto("waiting", "agent-1", Session, "claude", "Bash", input.RootElement.Clone(),
            null, false, false, DateTimeOffset.UtcNow.ToString("O")));
        await Assert.That(h.Broker.TryWithdrawTool("agent-1", Session, "toolu_p", toolName: "Bash", toolInput: input.RootElement.Clone())).IsFalse();
        await Assert.That(waiting.IsCompleted).IsFalse();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task Policy_ask_parks_and_records_the_prompt_it_raised() {
        await using var h = new Harness(Governed);
        await h.StartAsync();

        var response = h.PostAsync("gh pr merge");
        var pending  = await h.WaitPendingAsync();
        await Assert.That(h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app")).IsTrue();

        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
        await Assert.That(h.LogLines().Single()).Contains("\"source\":\"app\"");

        var evt = h.Server.PolicyEvents().Single();
        await Assert.That(evt.RequestedOutcome).IsEqualTo("ask");
        await Assert.That(evt.EffectiveOutcome).IsEqualTo("parked");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task A_call_no_rule_matches_parks_with_no_decision_event() {
        await using var h = new Harness(Governed);
        await h.StartAsync();

        var response = h.PostAsync("cargo build");
        var pending  = await h.WaitPendingAsync();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");

        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
        await Assert.That(h.Server.PolicyEvents().Length).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task An_attribution_carrying_no_snapshot_is_never_evaluated() {
        await using var h = new Harness(null);
        await h.StartAsync();

        var response = h.PostAsync("git push --force");
        var pending  = await h.WaitPendingAsync();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");

        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
        await Assert.That(h.Server.PolicyEvents().Length).IsEqualTo(0);
    }

    /// A terminal-hosted Codex is governed by its launch's policy exactly as Claude is.
    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task A_hosted_codex_request_is_answered_by_its_policy() {
        await using var h = new Harness(Governed);
        await h.StartAsync();

        var response = await h.PostAsync("git push --force", vendor: "codex");

        await Assert.That(await Harness.BehaviorOf(response)).IsEqualTo("deny");
        await Assert.That(h.Server.PolicyEvents().Single().Vendor).IsEqualTo("codex");
    }

    // === Codex auto-review: kcap policy first, then Codex's reviewer, never a card for an undecided call ===

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_auto_review_codex_call_no_rule_decides_is_left_to_codex(bool governed) {
        await using var h = new Harness(governed ? Governed : null, "auto_review");
        await h.StartAsync();

        var response = await h.PostAsync("cargo build", vendor: "codex");

        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("{}");
        await Assert.That(h.Broker.PendingSnapshot().Count).IsEqualTo(0);
        await Assert.That(h.Server.BeginCount).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task An_auto_review_codex_call_is_still_denied_by_policy() {
        await using var h = new Harness(Governed, "auto_review");
        await h.StartAsync();

        var response = await h.PostAsync("git push --force", vendor: "codex");

        await Assert.That(await Harness.BehaviorOf(response)).IsEqualTo("deny");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task An_auto_review_codex_call_a_policy_asks_about_still_reaches_a_person() {
        await using var h = new Harness(Governed, "auto_review");
        await h.StartAsync();

        var response = h.PostAsync("gh pr merge", vendor: "codex");
        var pending  = await h.WaitPendingAsync();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");

        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
    }

    /// Auto-review is Codex's; a Claude call attributed to such an agent takes the normal lane.
    [Test, NotInParallel(nameof(LocalPermissionBridgePolicyTests))]
    public async Task Auto_review_never_yields_a_claude_call() {
        await using var h = new Harness(null, "auto_review");
        await h.StartAsync();

        var response = h.PostAsync("cargo build");
        var pending  = await h.WaitPendingAsync();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");

        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
    }
}

/// Counts the server legs and captures the run events the bridge fires, and never answers a
/// permission request itself — so a request that reaches the human lane stays open until the
/// broker settles it.
sealed class PolicyServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        new FixedCredentialSource(),
        NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) {
    readonly List<object> _runEvents = [];
    int _beginCount;

    public int BeginCount => Volatile.Read(ref _beginCount);

    public PolicyDecisionEventV1[] PolicyEvents() {
        lock (_runEvents) return [.. _runEvents.OfType<PolicyDecisionEventV1>()];
    }

    public override Task AppendAgentRunEventAsync(string agentId, object evt) {
        lock (_runEvents) _runEvents.Add(evt);
        return Task.CompletedTask;
    }

    public override Task<string> BeginPermissionRequestAsync(
            string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions,
            CancellationToken ct, Func<bool> abandoned) {
        Interlocked.Increment(ref _beginCount);
        return Task.FromResult("srv-1");
    }

    public override Task<PermissionDecision> AwaitPermissionDecisionAsync(string serverRequestId, CancellationToken ct) =>
        new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);

    public override Task<RespondOutcome> RespondToPermissionAsync(string sessionId, string serverRequestId, PermissionDecision decision) =>
        Task.FromResult(new RespondOutcome(RespondOutcomeKind.Applied, null));

    /// The answer to a request no agent was attributed for; unset, it stays open like any other.
    public PermissionDecision? UnattributedAnswer { get; set; }

    public override Task<PermissionDecision> RequestPermissionAsync(
            string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct = default) =>
        UnattributedAnswer is { } answer ? Task.FromResult(answer) : base.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct);
}

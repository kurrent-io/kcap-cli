using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Grok;

/// <summary>
/// The hosted Grok launch shape: the argv and environment the child gets, and the <c>session/new</c>
/// it is sent, against the surface probed in <c>docs/probes/2026-10-13-grok-acp/</c>.
/// </summary>
public class GrokHostedLaunchTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    static RuntimeStartContext Ctx() =>
        new(AgentId: "agent-1", Vendor: "grok", SourceRepoPath: "/repo",
            Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: "",
            Model: null, Effort: null, Tools: null,
            IsReview: false, IsReviewFlow: false, Review: null,
            Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap");

    [Test]
    public async Task Descriptor_MatchesTheProbedSurface() {
        var descriptor = AcpVendorDescriptors.Grok;

        await Assert.That(descriptor.Vendor).IsEqualTo("grok");
        // A configured leader would hand the session to a shared process the daemon does not own.
        await Assert.That(descriptor.Argv.SequenceEqual(["agent", "--no-leader", "stdio"])).IsTrue();
        await Assert.That(descriptor.SupportsMcpServers).IsTrue();
        await Assert.That(descriptor.ModelSelector).IsEqualTo(ConfigOptionModelSelector.Instance);
        await Assert.That(descriptor.SupportsUnattended).IsTrue();
        await Assert.That(descriptor.UnattendedInteractionPolicy).IsEqualTo(AcpUnattendedInteractionPolicy.AllowlistedAutoApprove);
        await Assert.That(descriptor.ReviewFlowMcpTransport).IsEqualTo(AcpReviewFlowMcpTransport.SessionNew);
        await Assert.That(descriptor.SupportsBorrowedReviewFlow).IsFalse();
        await Assert.That(descriptor.SupportsReconnectResume).IsFalse();
    }

    [Test]
    public async Task ZeroConfiguration_ResolvesTheShippedBinaryName() {
        await Assert.That(new DaemonConfig().PathSlot(AcpVendorDescriptors.Grok.Harness).Read()).IsEqualTo("grok");
    }

    /// <summary>The user's own <c>permission_mode = "always-approve"</c> applies to an ACP session unless
    /// <c>session/new</c> overrides it, and then Grok never asks — so the daemon's broker and launch
    /// presets would decide nothing.</summary>
    [Test]
    public async Task SessionNew_turns_off_always_approve_so_the_daemon_decides_permissions() {
        var fake = new FakeAcpAgent();
        using var cts = new CancellationTokenSource();
        var run = fake.RunAsync(cts.Token);

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Grok,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: new StubServerConnection(),
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()),
            timeProvider: TimeProvider.System);

        var start = await factory.StartAsync(Ctx(), cts.Token).WaitAsync(HangGuard);

        var sessionNew = fake.ReceivedCalls.Single(c => c.Method == "session/new").Params!.Value;
        await Assert.That(sessionNew.GetProperty("_meta").GetProperty("yoloMode").GetBoolean()).IsFalse();

        await start.Runtime.DisposeAsync();
        await cts.CancelAsync();
        try { await run.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();
    }

    static async Task<JsonElement> SessionNewMcpServersAsync(
            RuntimeStartContext ctx, DaemonConfig? config = null, Func<string, string?>? resolveVendorVersion = null) {
        var fake = new FakeAcpAgent();
        using var cts = new CancellationTokenSource();
        var run = fake.RunAsync(cts.Token);

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Grok,
            config: config ?? new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: new StubServerConnection(),
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()),
            timeProvider: TimeProvider.System,
            resolveVendorVersion: resolveVendorVersion);

        var start   = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);
        var servers = fake.ReceivedCalls.Single(c => c.Method == "session/new").Params!.Value.GetProperty("mcpServers").Clone();

        await start.Runtime.DisposeAsync();
        await cts.CancelAsync();
        try { await run.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();

        return servers;
    }

    /// <summary>A hosted Grok gets every kcap server from the daemon, on the daemon's kcap, rather than
    /// whatever its imported Cursor config holds: Grok runs a <c>session/new</c> server in place of a
    /// same-named imported one, so a registration left on a removed binary cannot take a tool away.</summary>
    [Test]
    public async Task An_interactive_launch_brings_every_kcap_server_on_the_daemons_kcap() {
        var servers = await SessionNewMcpServersAsync(Ctx() with { ServerUrl = "https://kcap.example" });

        var expected = KcapMcpServers.ForHarness("grok").Select(s => JsonSerializer.Serialize(new {
            name = s.Name, command = "/usr/local/bin/kcap", args = s.Args, env = new[] { new { name = "KCAP_URL", value = "https://kcap.example" } },
        }));
        await Assert.That(servers.EnumerateArray().Select(s => s.GetRawText())).IsEquivalentTo(expected);
    }

    /// <summary>The daemon's kcap set is for interactive launches only: it carries <c>kcap-flows</c>,
    /// which would let a reviewer start a nested flow, so a review keeps exactly its own surface.</summary>
    [Test]
    public async Task A_review_launch_brings_only_its_result_channel() {
        using var daemons = new TempDaemonStore();
        var config = new DaemonConfig { GrokUnattendedReviewerEnabled = true, Store = daemons.Store, Name = "test-daemon", DaemonEpoch = "epoch-1" };
        AcpHostedAgentRuntimeFactory.VersionStoreFor(config, AcpVendorDescriptors.Grok.Vendor).Affirm("1.0.52");

        var servers = await SessionNewMcpServersAsync(
            Ctx() with { IsReviewFlow = true, ServerUrl = "http://kcap.test" }, config, _ => "1.0.52");

        var argvs = servers.EnumerateArray().Select(s => string.Join(" ", s.GetProperty("args").EnumerateArray().Select(a => a.GetString())));
        await Assert.That(argvs).IsEquivalentTo(["mcp flow-result"]);
    }

    /// <summary>A private agent's child is given nothing that ties its session to the server.</summary>
    [Test]
    public async Task A_private_launch_brings_no_kcap_server() {
        var servers = await SessionNewMcpServersAsync(Ctx() with { IsPrivate = true });

        await Assert.That(servers.GetRawText()).IsEqualTo("[]");
    }

    /// <summary>Grok reports a turn's usage only on its own extension method, so a launch that did not
    /// hand the runtime the Grok reader would record a hosted turn as free.</summary>
    [Test]
    public async Task A_hosted_turn_reports_its_token_usage() {
        var fake = new FakeAcpAgent();
        fake.EnqueuePromptScript(
            [JsonDocument.Parse($$"""{"jsonrpc":"2.0","method":"_x.ai/session_notification","params":{"sessionId":"{{FakeAcpAgent.FixedSessionId}}","update":{"sessionUpdate":"turn_completed","usage":{"modelUsage":{"grok-4.5-build":{"inputTokens":100,"cachedReadTokens":40,"outputTokens":7} } } } } }""").RootElement.Clone()],
            JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone());
        using var cts = new CancellationTokenSource();
        var run = fake.RunAsync(cts.Token);

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Grok,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: new StubServerConnection(),
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()),
            timeProvider: TimeProvider.System);

        var start = await factory.StartAsync(Ctx() with { Prompt = "hi" }, cts.Token).WaitAsync(HangGuard);
        var envelopes = ((AcpHostedAgentRuntime)start.Runtime).Envelopes;

        AcpEventEnvelope usage;
        do usage = await envelopes.ReadAsync().AsTask().WaitAsync(HangGuard);
        while (usage.Kind != AcpEventKind.TokenUsage);

        await Assert.That(usage.Model).IsEqualTo("grok-4.5-build");
        await Assert.That(usage.UsageInputTokens).IsEqualTo(100);
        await Assert.That(usage.UsageCachedInputTokens).IsEqualTo(40);

        await start.Runtime.DisposeAsync();
        await cts.CancelAsync();
        try { await run.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();
    }

    /// <summary>Grok runs a subagent as its own ACP session on the parent's connection, so its updates
    /// carry the child's sessionId; recorded as the parent's, its tool calls and replies would read as
    /// the hosted agent's own. A subagent's own subagent is announced under the subagent's sessionId,
    /// and a session never announced is no agent's.</summary>
    [Test]
    public async Task A_subagents_events_are_recorded_as_the_subagents() {
        const string child      = "01a14f26-a703-7de1-aa37-7eda7334fe82";
        const string agentId    = "01a14f26a7037de1aa377eda7334fe82";
        const string grandchild = "01a14f26-b000-7000-8000-000000000001";
        var parent = FakeAcpAgent.FixedSessionId;

        JsonElement Frame(string json) => JsonDocument.Parse(json).RootElement.Clone();
        JsonElement Update(string sid, string update) =>
            Frame("""{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":""" + $"\"{sid}\",\"update\":{update}}}}}");
        JsonElement Grok(string update, string? sid = null) =>
            Frame("""{"jsonrpc":"2.0","method":"_x.ai/session_notification","params":{"sessionId":""" + $"\"{sid ?? parent}\",\"update\":{update}}}}}");

        var fake = new FakeAcpAgent();
        fake.EnqueuePromptScript([
            Grok($$"""{"sessionUpdate":"subagent_spawned","subagent_id":"{{child}}","parent_session_id":"{{parent}}","child_session_id":"{{child}}","subagent_type":"general-purpose"}"""),
            Update(child, """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"read_file","kind":"read","status":"pending"}"""),
            Update(child, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello probe"}}"""),
            Update(child, """{"sessionUpdate":"session_info_update","title":"Subagent title"}"""),
            Update(child, """{"sessionUpdate":"usage_update","used":1000,"size":256000}"""),
            Update(child, """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"child-only","description":"x"}]}"""),
            Grok($$"""{"sessionUpdate":"subagent_spawned","child_session_id":"{{grandchild}}","subagent_type":"explore"}""", sid: child),
            Update(grandchild, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"nested"}}"""),
            Update("01a14f26-ffff-7fff-8fff-ffffffffffff", """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"stray"}}"""),
            Grok($$"""{"sessionUpdate":"subagent_finished","subagent_id":"{{child}}","child_session_id":"{{child}}","status":"completed"}"""),
            Update(parent, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"done"}}"""),
        ], JsonDocument.Parse("""{"stopReason":"end_turn"}""").RootElement.Clone());
        using var cts = new CancellationTokenSource();
        var run = fake.RunAsync(cts.Token);

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Grok,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: new StubServerConnection(),
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess()),
            timeProvider: TimeProvider.System);

        var start     = await factory.StartAsync(Ctx() with { Prompt = "hi" }, cts.Token).WaitAsync(HangGuard);
        var commands  = new List<string>();
        start.Runtime.OnCommandsAvailable = list => { lock (commands) commands.AddRange(list.Select(c => c.Name)); };
        var envelopes = ((AcpHostedAgentRuntime)start.Runtime).Envelopes;

        var seen = new List<AcpEventEnvelope>();
        while (!seen.Any(e => e is { Kind: AcpEventKind.AssistantText, Text: "done" }))
            seen.Add(await envelopes.ReadAsync().AsTask().WaitAsync(HangGuard));

        var started = seen.Where(e => e.Kind == AcpEventKind.SubagentStarted).ToList();
        await Assert.That(started.Select(e => (e.AgentId, e.AgentType)))
            .IsEquivalentTo(new[] { ((string?)agentId, (string?)"general-purpose"), (grandchild.Replace("-", ""), "explore") });
        await Assert.That(seen.Single(e => e.Kind == AcpEventKind.ToolCall).AgentId).IsEqualTo(agentId);
        await Assert.That(seen.Single(e => e is { Kind: AcpEventKind.AssistantText, Text: "hello probe" }).AgentId).IsEqualTo(agentId);
        await Assert.That(seen.Single(e => e.Kind == AcpEventKind.SubagentCompleted).AgentId).IsEqualTo(agentId);
        await Assert.That(seen.Single(e => e is { Kind: AcpEventKind.AssistantText, Text: "done" }).AgentId).IsNull();
        await Assert.That(seen.Single(e => e is { Kind: AcpEventKind.AssistantText, Text: "nested" }).AgentId)
            .IsEqualTo(grandchild.Replace("-", ""));
        await Assert.That(seen.Any(e => e.Text?.Contains("stray") == true)).IsFalse();
        await Assert.That(seen.Any(e => e.Kind is AcpEventKind.SessionTitle or AcpEventKind.Usage)).IsFalse();
        bool childCommandsPublished;
        lock (commands) childCommandsPublished = commands.Contains("child-only");
        await Assert.That(childCommandsPublished).IsFalse();

        await start.Runtime.DisposeAsync();
        await cts.CancelAsync();
        try { await run.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await fake.DisposeAsync();
    }

    [Test]
    public async Task Other_vendors_send_no_session_new_meta() {
        await Assert.That(AcpVendorDescriptors.OpenCode.SessionNewMeta is null).IsTrue();
        await Assert.That(AcpVendorDescriptors.Cursor.SessionNewMeta is null).IsTrue();
    }

    /// <summary>kcap's own Grok hook loads inside the hosted child and would record the session a
    /// second time; the variable names the daemon, whose direct child the hook then stands down in.</summary>
    [Test]
    public async Task Every_launch_marks_the_child_as_daemon_hosted() {
        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(AcpVendorDescriptors.Grok, new DaemonConfig(), Ctx());

        await Assert.That(psi.Environment[GrokHookEnvironment.HostedVar]).IsEqualTo(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task Other_vendors_are_not_marked_as_hosted_grok() {
        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            AcpVendorDescriptors.OpenCode, new DaemonConfig(), Ctx() with { Vendor = "opencode" });

        await Assert.That(psi.Environment.TryGetValue(GrokHookEnvironment.HostedVar, out var value) ? value : null)
            .IsEqualTo(Environment.GetEnvironmentVariable(GrokHookEnvironment.HostedVar));
    }

    [Test]
    public async Task Availability_TracksTheConfiguredBinary() {
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Grok,
            config: new DaemonConfig { GrokPath = "kcap-grok-that-does-not-exist", Binaries = BinaryProbe.FromEnvironment() },
            loggerFactory: NullLoggerFactory.Instance,
            connection: new StubServerConnection(),
            connectionSource: _ => throw new InvalidOperationException("IsAvailable must not spawn a process."),
            timeProvider: TimeProvider.System);

        await Assert.That(factory.Vendor).IsEqualTo("grok");
        await Assert.That(factory.IsAvailable()).IsFalse();
    }

    [Test]
    public async Task Launch_presets_route_through_the_permission_bridge() {
        await Assert.That(AcpPermissionPresets.RoutedVendors).Contains("grok");
    }

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;
        public Task TerminateAsync(TimeSpan? timeout = null) { HasExited = true; ExitCode = 0; _exited.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class StubServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        new FixedCredentialSource(),
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance, TimeProvider.System);
}

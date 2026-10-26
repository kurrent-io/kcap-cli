using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeSessionStartClaimTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    const string Server = "https://kcap.example";
    const string Session = "aaaaaaaa111144448888bbbbbbbbbbbb";
    const string OldToken = "lx:another-boot:1";

    static Task<bool> NeverSuppress(string _) => Task.FromResult(false);

    static string Hook(string source, string eventName = "SessionStart") => new JsonObject {
        ["hook_event_name"] = eventName, ["session_id"] = "aaaaaaaa-1111-4444-8888-bbbbbbbbbbbb",
        ["source"] = source, ["transcript_path"] = "/t/resumed.jsonl", ["cwd"] = "/repo",
    }.ToJsonString();

    void DeadClaim(int pid, string session) {
        var note = Config.Root.Path("agent-sessions", pid.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        File.WriteAllText(note, $"{session}\n{OldToken}");
        File.WriteAllText(note + ".origin", string.Join('\n',
            new[] { OldToken, "claude", Server, "/t/old.jsonl", "/repo" }.Select(Uri.EscapeDataString)));
    }

    [Test]
    [Arguments("resume", "SessionStart")]
    [Arguments("compact", "SessionStart")]
    [Arguments("resume", "sessionStart")]
    [Arguments("compact", "sessionStart")]
    [Arguments("resume", "session_start")]
    [Arguments("compact", "session_start")]
    [Arguments("resume", "session-start")]
    [Arguments("compact", "session-start")]
    public async Task A_live_session_start_is_not_ended_by_the_global_orphan_drain(string source, string eventName) {
        DeadClaim(900_001, Session);
        DeadClaim(900_002, "true-orphan");
        var body = Hook(source, eventName);
        var prepared = await ClaudeSessionStartClaim.PrepareAsync(body, Config.Root, () => Environment.ProcessId, NeverSuppress);
        var lifecycle = new HookSpool(Config.Root, TimeProvider.System);
        var http = new FixedCapacitorHttpClient();
        var profiles = Resolutions.At(Server, Config.Root);
        var poster = new AgentHookPoster(Config.Root, profiles, http,
            TestWatchers.For(Config.Root, profiles, http), TimeProvider.System);

        await poster.DrainSpoolsCoreAsync(lifecycle, new TranscriptSpool(Config.Root, TimeProvider.System), null,
            _ => Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.Expired)));

        await Assert.That(lifecycle.HasBacklog(Session)).IsFalse();
        await Assert.That(AgentSessions.OnThisMachine(Config.Root).LiveClaimant(SessionId.Parse(Session)!)).IsEqualTo(Environment.ProcessId);
        await Assert.That(prepared).IsEqualTo(body);
        string? route = null, end = null;
        await lifecycle.DrainAllAsync("trueorphan", (r, b) => {
            route = r; end = b;
            return Task.FromResult(DrainOutcome.Delivered);
        }, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.That(route).IsEqualTo("session-end/claude");
        await Assert.That(JsonNode.Parse(end!)!["reason"]!.GetValue<string>()).IsEqualTo("parent_exited");
    }

    [Test]
    [Arguments("SessionStart")]
    [Arguments("sessionStart")]
    [Arguments("session_start")]
    [Arguments("session-start")]
    public async Task A_clear_start_preserves_its_predecessor_before_claiming_the_new_session(string eventName) {
        var sessions = AgentSessions.OnThisMachine(Config.Root);
        sessions.Claim(Environment.ProcessId, SessionId.Parse("previous")!);

        var prepared = await ClaudeSessionStartClaim.PrepareAsync(Hook("clear", eventName), Config.Root, () => Environment.ProcessId, NeverSuppress);

        await Assert.That(JsonNode.Parse(prepared)?["previous_session_id"]?.GetValue<string>()).IsEqualTo("previous");
        await Assert.That(sessions.Of(Environment.ProcessId)?.Value).IsEqualTo(Session);
    }

    [Test]
    [Arguments("not json")]
    [Arguments("null")]
    [Arguments("{\"hook_event_name\":\"SessionEnd\",\"session_id\":\"other\"}")]
    [Arguments("{\"hook_event_name\":\"SessionStart\",\"session_id\":\"../bad\"}")]
    [Arguments("{\"hook_event_name\":\"SessionStart\",\"session_id\":\"other\"}")]
    public async Task A_body_that_cannot_prove_a_session_start_does_not_replace_a_claim(string body) {
        var sessions = AgentSessions.OnThisMachine(Config.Root);
        sessions.Claim(Environment.ProcessId, SessionId.Parse("previous")!);

        await Assert.That(await ClaudeSessionStartClaim.PrepareAsync(body, Config.Root, () => Environment.ProcessId, NeverSuppress)).IsEqualTo(body);
        await Assert.That(sessions.Of(Environment.ProcessId)?.Value).IsEqualTo("previous");
    }

    [Test]
    public async Task An_unresolved_agent_ancestor_does_not_claim_the_hook_process() {
        var body = Hook("resume");

        await Assert.That(await ClaudeSessionStartClaim.PrepareAsync(body, Config.Root, () => null, NeverSuppress)).IsEqualTo(body);
        await Assert.That(AgentSessions.OnThisMachine(Config.Root).LiveClaimant(SessionId.Parse(Session)!)).IsNull();
    }

    [Test]
    [ParallelLimiter<SubprocessLimit>]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_suppressed_start_cannot_queue_an_end_when_its_process_exits(bool excluded) {
        using var agent = DummyProcess.StartSleep(60);
        if (!excluded) DisabledSessions.Mark(Session, Config.Root);
        var profiles = Resolutions.Of(new Profile { ExcludedPaths = excluded ? ["/repo"] : [] }, serverUrl: "ftp://host");
        var http = new FixedCapacitorHttpClient();
        var spool = new HookSpool(Config.Root, TimeProvider.System);
        var clock = new HookClock(TimeProvider.System);
        var command = new ClaudeHookCommand(Config.Root, profiles, clock, Home,
            TestHarnesses.Under(Home), HostedAgent.Terminal, http, TestWatchers.For(Config.Root, profiles, http),
            FakeProcessStarter.Refusing(), new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory), TestAccounts.None);
        var body = Hook("startup");
        var prepared = await ClaudeSessionStartClaim.PrepareAsync(body, Config.Root, () => agent.Pid,
            id => command.ShouldSuppressCaptureAsync(id, body, "session-start", profiles.Effective,
                clock.Budget(ClaudeHookCommand.SessionStartCeiling)));
        await Assert.That(AgentSessions.OnThisMachine(Config.Root).IsClaimed(SessionId.Parse(Session)!)).IsFalse();
        await Assert.That(AgentSessions.OnThisMachine(Config.Root).LiveClaimant(SessionId.Parse(Session)!)).IsNull();

        await command.HandleWithDeps(spool, new StringReader(prepared),
            () => throw new InvalidOperationException("an unusable URL must not construct a client"), TextWriter.Null);
        await Assert.That(spool.HasBacklog(Session)).IsFalse();
        agent.Kill();
        await Assert.That(agent.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();

        var exited = AgentSessions.OnThisMachine(Config.Root).ReapExited(Server);
        await Assert.That(exited).IsEmpty();
        AgentHookPoster.SpoolEnds(exited, Server, spool, TimeProvider.System);

        await Assert.That(spool.HasBacklog(Session)).IsFalse();
    }

    [Test]
    public async Task A_capture_probe_failure_preserves_the_body_and_existing_claim() {
        var sessions = AgentSessions.OnThisMachine(Config.Root);
        sessions.Claim(Environment.ProcessId, SessionId.Parse("previous")!);
        var body = Hook("clear");

        var prepared = await ClaudeSessionStartClaim.PrepareAsync(body, Config.Root, () => Environment.ProcessId,
            _ => Task.FromException<bool>(new IOException("capture probe failed")));

        await Assert.That(prepared).IsEqualTo(body);
        await Assert.That(sessions.Of(Environment.ProcessId)?.Value).IsEqualTo("previous");
    }
}

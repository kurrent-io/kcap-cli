using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

public class MuseHookCommandTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string ParentId       = "01a13694-b0ad-7ae2-8bf5-2af6357a0847";
    const string ParentDashless = "01a13694b0ad7ae28bf52af6357a0847";
    const string ReminderChild  = "5673a002-a310-4979-b885-acbf0991e990";
    const string WorkerChild    = "66d6cfba-1cf6-45b3-a2a8-e5c60f4fafc9";

    static readonly DateOnly Today = new(2026, 10, 13);

    readonly WireMockServer     _server  = WireMockServer.Start();
    readonly FakeWatcherSpawner _spawner = new();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task Parent_session_id_comes_from_the_environment_only() {
        await Assert.That(MuseHookCommand.ParentSessionId(k => k == "MUSE_SESSION_ID" ? ParentId : null))
            .IsEqualTo(ParentDashless);
        await Assert.That(MuseHookCommand.ParentSessionId(_ => null)).IsNull();
        await Assert.That(MuseHookCommand.ParentSessionId(_ => "not-a-uuid")).IsNull();
    }

    [Test]
    public async Task Cap_keeps_the_marker_first_and_drops_whole_lines() {
        var fragment = "<!-- kcap-memory-index:v1 -->\n" + string.Join("\n", Enumerable.Range(0, 400).Select(i => $"- memory line {i} " + new string('x', 60)));
        var capped   = MuseHookCommand.CapFragment(fragment, 4096)!;
        await Assert.That(capped.StartsWith("<!-- kcap-memory-index:v1 -->\n", StringComparison.Ordinal)).IsTrue();
        await Assert.That(Encoding.UTF8.GetByteCount(capped)).IsLessThanOrEqualTo(4096);
        await Assert.That(capped.EndsWith('\n')).IsTrue();
        await Assert.That(capped).Contains("- memory line 0 ");
        await Assert.That(MuseHookCommand.CapFragment("short\n", 4096)).IsEqualTo("short\n");
        await Assert.That(MuseHookCommand.CapFragment(null, 4096)).IsNull();
    }

    [Test]
    [Arguments("skill-reminder", true)]
    [Arguments("goal-reminder", true)]
    [Arguments("66d6cfba-1cf6-45b3-a2a8-e5c60f4fafc9", false)]
    public async Task Reminder_ids_are_the_non_uuid_subagent_ids(string id, bool reminder) =>
        await Assert.That(MuseSubagentDiscovery.IsReminderAgentId(id)).IsEqualTo(reminder);

    /// <summary>The probe's reminder payload carries the child's own id as <c>session_id</c>; without
    /// <c>MUSE_SESSION_ID</c> nothing names a parent, so posting anything would mint a stray session.
    /// The same payload with the variable set posts, so the silence is the variable's doing.</summary>
    [Test]
    public async Task SubagentStart_without_parent_env_posts_nothing_and_exits_zero() {
        StubHooks();
        StageParentLog();
        var stdout  = new StringWriter();
        var payload = $$"""{"hook_event_name":"SubagentStart","subagent_id":"goal-reminder","child_session_id":"0ca3b023-bb2b-4d0b-b9f8-a3a1f6ef5d4b","session_id":"0ca3b023-bb2b-4d0b-b9f8-a3a1f6ef5d4b","turn_id":"0ca3b023-bb2b-4d0b-b9f8-a3a1f6ef5d4b","cwd":"{{AbsentCwd()}}","transcript_path":null,"model":"unknown","permission_mode":"default"}""";

        var exit = await Command().Handle(new StringReader(payload), stdout, _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();

        await Command().Handle(new StringReader(payload), new StringWriter(), ParentEnv, Today);
        await Assert.That(HookPosts("/hooks/subagent-start").Count).IsEqualTo(1);
    }

    [Test]
    public async Task SessionStart_with_a_log_posts_session_start_naming_the_dated_log_and_starts_the_watcher() {
        StubHooks();
        var log = StageParentLog();

        var exit = await Command().Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        var posts = HookPosts("/hooks/session-start/muse");
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(posts[0]).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(ParentDashless);
        await Assert.That(body.GetProperty("transcript_path").GetString()).IsEqualTo(log);
        await Assert.That(body.GetProperty("source").GetString()).IsEqualTo("startup");
        await Assert.That(body.GetProperty("hook_event_name").GetString()).IsEqualTo("SessionStart");

        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(_spawner.Spawned[0].Key).IsEqualTo(ParentDashless);
        await Assert.That(_spawner.Spawned[0].TranscriptPath).IsEqualTo(log);
        await Assert.That(_spawner.Spawned[0].Vendor).IsEqualTo("muse");
    }

    /// <summary>Muse fires SessionStart before it creates the log; the hook must not record a path
    /// Muse has not written, and leaves the start to a later hook.</summary>
    [Test]
    public async Task SessionStart_before_the_log_exists_posts_nothing_and_exits_zero() {
        StubHooks();

        var exit = await Command().Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(HookPosts("/hooks/").Count).IsEqualTo(0);
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SessionStart_resume_rearms_the_watcher_without_a_second_start() {
        StubHooks();
        var log = StageParentLog();

        var exit = await Command().Handle(new StringReader(SessionStartPayload("resume")), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(HookPosts("/hooks/").Count).IsEqualTo(0);
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(_spawner.Spawned[0].TranscriptPath).IsEqualTo(log);
        await Assert.That(_spawner.Spawned[0].Vendor).IsEqualTo("muse");
    }

    /// <summary>A log is staged under the child's id so that only the environment check stands
    /// between this hook and a second top-level session.</summary>
    [Test]
    public async Task SessionStart_from_a_child_session_does_nothing() {
        StubHooks();
        StageParentLog();
        var childLog = new MusePaths(Home, null, null).SessionLogPath(WorkerChild, Today)!;
        Home.CreateFile(Path.GetRelativePath(Home.Path, childLog), "{}\n");

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SessionStart","source":"startup","session_id":"{{WorkerChild}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(HookPosts("/hooks/").Count).IsEqualTo(0);
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SessionStart_with_a_non_uuid_session_id_does_nothing() {
        StubHooks();
        StageParentLog();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SessionStart","source":"startup","session_id":"../../etc","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SessionStart_for_a_disabled_session_does_nothing() {
        StubHooks();
        StageParentLog();
        DisabledSessions.Mark(ParentDashless, Config.Root);

        var exit = await Command().Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(HookPosts("/hooks/").Count).IsEqualTo(0);
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubagentStart_with_the_parent_env_posts_a_reminder_subagent_start() {
        StubHooks();
        var parentLog = StageParentLog();
        var stdout    = new StringWriter();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SubagentStart","subagent_id":"skill-reminder","child_session_id":"{{ReminderChild}}","session_id":"{{ReminderChild}}","turn_id":"{{ReminderChild}}","cwd":"{{AbsentCwd()}}","transcript_path":null,"model":"unknown","permission_mode":"default"}"""),
            stdout, ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        var posts = HookPosts("/hooks/subagent-start");
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(posts[0]).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(ParentDashless);
        await Assert.That(body.GetProperty("agent_id").GetString()).IsEqualTo(ReminderChild.Replace("-", ""));
        await Assert.That(body.GetProperty("agent_type").GetString()).IsEqualTo("skill-reminder");
        await Assert.That(body.GetProperty("transcript_path").GetString())
            .IsEqualTo(MusePaths.ChildSessionLogPath(parentLog, ReminderChild));
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SubagentStop_for_a_worker_posts_subagent_stop() {
        StubHooks();
        StageParentLog();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SubagentStop","subagent_id":"{{WorkerChild}}","child_session_id":"{{WorkerChild}}","stop_hook_active":false,"last_assistant_message":"done","session_id":"{{WorkerChild}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        var posts = HookPosts("/hooks/subagent-stop");
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(posts[0]).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(ParentDashless);
        await Assert.That(body.GetProperty("agent_type").GetString()).IsEqualTo("worker");
    }

    [Test]
    public async Task SessionEnd_posts_session_end_with_the_reason() {
        StubHooks();
        StageParentLog();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SessionEnd","reason":"other","session_id":"{{ParentId}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        var posts = HookPosts("/hooks/session-end/muse");
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(posts[0]).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(ParentDashless);
        await Assert.That(body.GetProperty("reason").GetString()).IsEqualTo("other");
        await Assert.That(body.TryGetProperty("ended_at", out _)).IsTrue();
    }

    [Test]
    [Arguments("UserPromptSubmit")]
    [Arguments("Stop")]
    [Arguments("PreToolUse")]
    public async Task Other_events_post_nothing_and_write_nothing(string eventName) {
        StubHooks();
        StageParentLog();
        var stdout = new StringWriter();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"{{eventName}}","prompt":"hello","session_id":"{{ParentId}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            stdout, ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();
    }

    [Test]
    public async Task A_malformed_payload_exits_zero() {
        var exit = await Command().Handle(new StringReader("{not json"), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    static string? ParentEnv(string key) => key == "MUSE_SESSION_ID" ? ParentId : null;

    MuseHookCommand Command() {
        var profiles = Resolutions.At(_server.Url!, Config.Root);
        var http     = new FixedCapacitorHttpClient();

        return new MuseHookCommand(Config.Root, profiles, new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home),
            HostedAgent.Terminal, http, TestWatchers.For(Config.Root, profiles, http, _spawner), new GitProviderRouter(),
            new WorkingDirectory(AppContext.BaseDirectory));
    }

    string StageParentLog() {
        var paths = new MusePaths(Home, null, null);
        var log   = paths.SessionLogPath(ParentId, Today)!;

        return Home.CreateFile(Path.GetRelativePath(Home.Path, log), "{}\n");
    }

    // A cwd that does not exist keeps repository detection from spawning git.
    string AbsentCwd() => Tmp.PathTo("missing").Replace("\\", "\\\\");

    string SessionStartPayload(string source) =>
        $$"""{"hook_event_name":"SessionStart","source":"{{source}}","session_id":"{{ParentId}}","cwd":"{{AbsentCwd()}}","transcript_path":null,"model":"unknown","permission_mode":"default"}""";

    void StubHooks() =>
        _server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

    List<string> HookPosts(string pathPrefix) =>
        _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
            .Select(e => e.RequestMessage.Body ?? "")
            .ToList();
}

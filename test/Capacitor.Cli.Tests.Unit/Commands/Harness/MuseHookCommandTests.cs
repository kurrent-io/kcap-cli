using System.Text;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Harness.Muse;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;
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

    const string AnnouncedSubagentId = "01a13565-a5b7-7071-874a-8c22f461fc24";
    const string AnnouncedChild      = "01a13565-a626-7f70-bda0-1c19ba4deadf";

    // Verbatim from a real Muse parent log, admission blobs dropped.
    const string WorkerSpawnLine =
        """{"schema_version":1,"id":"533c1cc9-29e6-586c-a278-1752d20fcc59","stream":{"kind":"session","id":"01a13563-ad7b-7b51-b12f-b2f4b78b6a1e"},"sequence":280,"recorded_at":1791897216448194,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"subagent.control.spawn_accepted","payload_schema_version":4,"payload":{"kind":"subagent_control","record":{"kind":"spawn_accepted","command_id":"test-coverage-integrations-01a13563-1","parent_session_id":"01a13563-ad7b-7b51-b12f-b2f4b78b6a1e","parent_run_id":"01a13565-88d0-72c0-ab37-cea217cfd40e","subagent_id":"01a13565-a5b7-7071-874a-8c22f461fc24","agent_path":"main/Test coverage auditor — Integrations group/1","depth":1,"role":"Test coverage auditor — Integrations group","task_id":"01a13565-a5a9-7330-a03b-3febeab2f755","task_stream":"task/01a13565-a5a9-7330-a03b-3febeab2f755","objective":"audit"}}}""";

    const string WorkerBoundLine =
        """{"schema_version":1,"id":"ed976e63-54a5-4471-a351-03225dc9a492","stream":{"kind":"session","id":"01a13563-ad7b-7b51-b12f-b2f4b78b6a1e"},"sequence":288,"recorded_at":1791897216551211,"record_type":"event","durability":"durable","causation_id":null,"payload_type":"subagent.control.child_session_bound","payload_schema_version":1,"payload":{"kind":"subagent_control","record":{"kind":"child_session_bound","schema_version":1,"subagent_id":"01a13565-a5b7-7071-874a-8c22f461fc24","child_session_id":"01a13565-a626-7f70-bda0-1c19ba4deadf","storage_layout":"nested_session_v1"}}}""";

    static readonly DateOnly Today = new(2026, 10, 13);

    readonly WireMockServer     _server  = WireMockServer.Start();
    readonly FakeWatcherSpawner _spawner = new();

    public void Dispose() => _server.Stop();

    async Task WarmServer() {
        // Server startup/JIT is outside the hook's real five-second budget. The tests
        // assert hook ordering and payloads; only the stalled-server case measures time.
        using var client = new HttpClient();
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.GetAsync($"{_server.Url}/__warmup", guard.Token);
        _server.ResetLogEntries();
    }

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
        await Assert.That(DateTimeOffset.TryParse(body.GetProperty("started_at").GetString(), out _)).IsTrue();

        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(_spawner.Spawned[0].Key).IsEqualTo(ParentDashless);
        await Assert.That(_spawner.Spawned[0].TranscriptPath).IsEqualTo(log);
        await Assert.That(_spawner.Spawned[0].Vendor).IsEqualTo("muse");
    }

    /// <summary>A hook inherits no <c>KCAP_AGENT_ID</c> from a daemon-hosted Muse, so the marker the daemon
    /// wrote is the only link to the agent. The command's config root is not the home-derived one, so the
    /// marker is found only by looking where a cleared-environment hook would.</summary>
    [Test]
    public async Task SessionStart_without_a_hosted_environment_takes_agent_host_id_from_the_marker() {
        StubHooks();
        StageParentLog();
        MuseHostedSessionMarker.Write(Home,
            new MuseHostedSessionMarker(ParentId, "agent-from-marker", "daemon-1", "epoch-1", DateTimeOffset.UtcNow));

        await Command().Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        var body = JsonDocument.Parse(HookPosts("/hooks/session-start/muse").Single()).RootElement;
        await Assert.That(body.GetProperty("agent_host_id").GetString()).IsEqualTo("agent-from-marker");
    }

    /// <summary>A hook for a session hosted by a daemon with its own config root records to that root's
    /// profile: the home-derived root a cleared environment resolves names no server at all, so the post
    /// reaching the stub proves the marker's root was used.</summary>
    [Test]
    public async Task A_marker_naming_a_config_root_makes_the_hook_post_through_that_roots_profile() {
        StubHooks();
        StageParentLog();
        var daemonRoot = DaemonRoot(_server.Url!);
        WriteMarker(daemonRoot, serverUrl: null);

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);
        var (root, profiles) = await Resolve(connection, () => ConfigRoot.UnderHome(Home.Path));

        await Assert.That(root.Directory).IsEqualTo(daemonRoot);
        await Assert.That((await AppConfig.ResolveActiveProfile([], ConfigRoot.UnderHome(Home.Path), ProfileOverrides.None, NoLocalInstallation.Endpoints)).Resolution.ServerUrl)
            .IsNull();

        await Command(root: root, profiles: profiles).Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        var body = JsonDocument.Parse(HookPosts("/hooks/session-start/muse").Single()).RootElement;
        await Assert.That(body.GetProperty("agent_host_id").GetString()).IsEqualTo("agent-from-marker");
    }

    /// <summary>The daemon's server outranks the root's active profile, as the relay's <c>KCAP_URL</c> does.</summary>
    [Test]
    public async Task A_marker_naming_a_server_overrides_the_roots_active_profile() {
        StubHooks();
        StageParentLog();
        WriteMarker(DaemonRoot("http://127.0.0.1:9"), _server.Url);

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);
        var (root, profiles) = await Resolve(connection, () => ConfigRoot.UnderHome(Home.Path));

        await Assert.That(profiles.Resolution.ServerUrl).IsEqualTo(AppConfig.NormalizeUrl(_server.Url!));

        await Command(root: root, profiles: profiles).Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(HookPosts("/hooks/session-start/muse").Count).IsEqualTo(1);
    }

    /// <summary>A default-root daemon pointed elsewhere by <c>KCAP_URL</c>: the hook shares its root, but the
    /// root's active profile names another server, so only the marker's server lands the post on the stub.</summary>
    [Test]
    public async Task A_marker_server_redirects_a_default_root_hook_from_the_active_profile() {
        StubHooks();
        StageParentLog();
        HomeRoot("http://127.0.0.1:9");
        WriteMarker(configDir: null, _server.Url);

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);
        var (root, profiles) = await Resolve(connection, () => ConfigRoot.UnderHome(Home.Path));

        await Assert.That(root.Directory).IsEqualTo(ConfigRoot.UnderHome(Home.Path).Directory);
        await Assert.That(profiles.Resolution.ServerUrl).IsEqualTo(AppConfig.NormalizeUrl(_server.Url!));

        await Command(root: root, profiles: profiles).Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(HookPosts("/hooks/session-start/muse").Count).IsEqualTo(1);
    }

    /// <summary>The hook takes the marker profile's credential and posts to the marker's server, even
    /// when the profile's own URL differs.</summary>
    [Test]
    public async Task A_marker_profile_supplies_the_credential_while_the_marker_server_stays_the_destination() {
        StubHooks();
        StageParentLog();
        WriteMarker(DaemonRoot("http://127.0.0.1:9", teamBUrl: "http://127.0.0.1:9"), _server.Url, profile: "team-b");

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);
        var (root, profiles) = await Resolve(connection, () => ConfigRoot.UnderHome(Home.Path));

        await Assert.That(profiles.Name).IsEqualTo("team-b");
        await Assert.That(profiles.Resolution.ServerUrl).IsEqualTo(AppConfig.NormalizeUrl(_server.Url!));

        await Command(root: root, profiles: profiles).Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(HookPosts("/hooks/session-start/muse").Count).IsEqualTo(1);
    }

    /// <summary>A profile removed mid-session leaves only the active profile's credential to fall back on,
    /// so the hook is refused rather than recorded under another user.</summary>
    [Test]
    public async Task A_marker_profile_removed_from_the_config_refuses_the_hook() {
        WriteMarker(DaemonRoot(_server.Url!), _server.Url, profile: "team-b");

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);
        var (_, profiles) = await Resolve(connection, () => ConfigRoot.UnderHome(Home.Path));

        await Assert.That(profiles.Name).IsEqualTo("default");
        await Assert.That(connection.MissingProfileWarning(profiles)).Contains("'team-b'");
    }

    [Test]
    public async Task A_marker_profile_present_in_the_config_is_not_refused() {
        WriteMarker(DaemonRoot("http://127.0.0.1:9", teamBUrl: _server.Url), _server.Url, profile: "team-b");

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);
        var (_, profiles) = await Resolve(connection, () => ConfigRoot.UnderHome(Home.Path));

        await Assert.That(connection.MissingProfileWarning(profiles)).IsNull();
    }

    /// <summary>A marker that names a profile but no usable server leaves the destination to the profile.</summary>
    [Test]
    public async Task A_marker_profile_without_a_server_resolves_through_the_profile() {
        WriteMarker(DaemonRoot("http://127.0.0.1:9", teamBUrl: _server.Url), "file:///etc/passwd", profile: "team-b");

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);
        var (_, profiles) = await Resolve(connection, () => ConfigRoot.UnderHome(Home.Path));

        await Assert.That(profiles.Name).IsEqualTo("team-b");
        await Assert.That(profiles.Resolution.ServerUrl).IsEqualTo(AppConfig.NormalizeUrl(_server.Url!));
    }

    /// <summary>An environment that chooses a server by either means keeps it; the marker's root still applies.</summary>
    [Test]
    public async Task An_environment_profile_outranks_the_marker_server_but_not_its_root() {
        var daemonRoot = DaemonRoot(_server.Url!);
        WriteMarker(daemonRoot, _server.Url, profile: "team-b");

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home,
            k => k == ProfileOverrides.ProfileVar ? "mine" : null);

        await Assert.That(connection).IsEqualTo(new MuseHookConnection(daemonRoot, null, null));
    }

    /// <summary>A child's hook names its own session; the marker belongs to the parent it runs under.</summary>
    [Test]
    public async Task A_child_hook_finds_the_marker_under_its_parent_session() {
        var daemonRoot = DaemonRoot(_server.Url!);
        WriteMarker(daemonRoot, _server.Url);
        var child = $$"""{"hook_event_name":"SubagentStart","child_session_id":"{{WorkerChild}}","session_id":"{{WorkerChild}}"}""";

        var connection = MuseHookConnection.Resolve(child, Home, ParentEnv);

        await Assert.That(connection).IsEqualTo(new MuseHookConnection(daemonRoot, _server.Url, null));
    }

    /// <summary>What the environment sets wins: a relayed hook already carries the daemon's own values.</summary>
    [Test]
    public async Task The_environment_outranks_the_marker() {
        WriteMarker(DaemonRoot(_server.Url!), _server.Url);

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home,
            k => k is ConfigRoot.ConfigDirEnvVar or ProfileOverrides.UrlVar ? "set" : null);

        await Assert.That(connection).IsEqualTo(MuseHookConnection.None);
    }

    /// <summary>The absolute root is built from the temp directory: a POSIX literal is not fully qualified
    /// on Windows.</summary>
    [Test]
    [Arguments(false, "https://kcap.example", true)]
    [Arguments(true, "file:///etc/passwd", false)]
    [Arguments(true, "not a url", false)]
    public async Task A_marker_value_that_is_not_an_absolute_root_or_http_url_is_ignored(
            bool absoluteDir, string serverUrl, bool urlAdopted) {
        var configDir = absoluteDir ? Tmp.PathTo("abs-config") : Path.Join("relative", "config");
        WriteMarker(configDir, serverUrl);

        var connection = MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null);

        await Assert.That(connection).IsEqualTo(
            new MuseHookConnection(absoluteDir ? configDir : null, urlAdopted ? serverUrl : null, null));
    }

    [Test]
    public async Task No_marker_names_no_connection() =>
        await Assert.That(MuseHookConnection.Resolve(SessionStartPayload("startup"), Home, _ => null)).IsEqualTo(MuseHookConnection.None);

    [Test]
    public async Task SessionStart_without_a_marker_or_hosted_environment_forwards_no_agent_host_id() {
        StubHooks();
        StageParentLog();

        await Command().Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        var body = JsonDocument.Parse(HookPosts("/hooks/session-start/muse").Single()).RootElement;
        await Assert.That(body.TryGetProperty("agent_host_id", out _)).IsFalse();
    }

    /// <summary>The end is linked to the same agent as the start, through the marker it then deletes.</summary>
    [Test]
    public async Task SessionEnd_takes_agent_host_id_from_the_marker_then_deletes_it() {
        StubHooks();
        MuseHostedSessionMarker.Write(Home,
            new MuseHostedSessionMarker(ParentId, "agent-from-marker", "daemon-1", "epoch-1", DateTimeOffset.UtcNow));

        await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SessionEnd","reason":"other","session_id":"{{ParentId}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        var body = JsonDocument.Parse(HookPosts("/hooks/session-end/muse").Single()).RootElement;
        await Assert.That(body.GetProperty("agent_host_id").GetString()).IsEqualTo("agent-from-marker");
        await Assert.That(MuseHostedSessionMarker.TryRead(Home, ParentId)).IsNull();
    }

    /// <summary>Stop only starts a watcher, so this is the start's one chance: it is posted without a
    /// path Muse has not written, and no watcher starts until the log exists.</summary>
    [Test]
    public async Task SessionStart_when_no_log_is_found_posts_the_start_without_a_transcript_path() {
        StubHooks();

        var exit = await Command().Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        var posts = HookPosts("/hooks/session-start/muse");
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(posts[0]).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(ParentDashless);
        await Assert.That(body.TryGetProperty("transcript_path", out _)).IsFalse();
        await Assert.That(DateTimeOffset.TryParse(body.GetProperty("started_at").GetString(), out _)).IsTrue();
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(0);
    }

    /// <summary>Muse runs hooks with no <c>XDG_DATA_HOME</c>; the data root persisted at install is
    /// what finds a log under a custom root.</summary>
    [Test]
    public async Task SessionStart_with_a_cleared_environment_finds_the_log_under_the_persisted_data_root() {
        StubHooks();
        string data = Tmp.CreateDir("custom-data");
        MuseHookRoots.Persist(Home, new MusePaths(Home, null, data));
        var log = new MusePaths(Home, null, data).SessionLogPath(ParentId, Today)!;
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllTextAsync(log, "{}\n");
        var harnesses = HarnessRegistry.Over(TestBinaries.None, MuseHarness.FromEnvironment(Home, _ => null));

        var exit = await Command(harnesses: harnesses).Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(_spawner.Spawned[0].TranscriptPath).IsEqualTo(log);
        var body = JsonDocument.Parse(HookPosts("/hooks/session-start/muse").Single()).RootElement;
        await Assert.That(body.GetProperty("transcript_path").GetString()).IsEqualTo(log);
    }

    /// <summary>The watcher starts before the session-start POST is attempted, so a server that
    /// refuses or stalls the POST, or a hook Muse kills mid-POST, still leaves capture running.</summary>
    [Test]
    [Arguments(500)]
    [Arguments(400)]
    public async Task SessionStart_starts_the_watcher_before_the_post_whatever_the_server_answers(int status) {
        _server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status));
        var log         = StageParentLog();
        var postsAtSpawn = -1;
        var spawner     = new FakeWatcherSpawner(_ => {
            postsAtSpawn = HookPosts("/hooks/").Count;
            return Task.CompletedTask;
        });

        var exit = await Command(spawner).Handle(new StringReader(SessionStartPayload("startup")), new StringWriter(), _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(spawner.Spawned[0].TranscriptPath).IsEqualTo(log);
        await Assert.That(postsAtSpawn).IsEqualTo(0);
        await Assert.That(HookPosts("/hooks/session-start/muse").Count).IsEqualTo(1);
    }

    /// <summary>A SessionStart that was killed or found no log leaves no watcher; the end of the next
    /// turn starts one, without posting.</summary>
    [Test]
    public async Task Stop_rearms_a_missing_watcher_without_posting() {
        StubHooks();
        var log    = StageParentLog();
        var stdout = new StringWriter();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"done","session_id":"{{ParentId}}","turn_id":"3a75e7fd-2b07-401b-9446-6af740e7933d","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            stdout, ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(_spawner.Spawned[0].Key).IsEqualTo(ParentDashless);
        await Assert.That(_spawner.Spawned[0].TranscriptPath).IsEqualTo(log);
        await Assert.That(_spawner.Spawned[0].Vendor).IsEqualTo("muse");
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

    /// <summary>The server keeps the first label a subagent is registered with, and the hook fires
    /// before the watcher's scan, so a worker the parent log already announced carries its role.</summary>
    [Test]
    public async Task SubagentStart_for_an_announced_worker_takes_its_role_from_the_parent_log() {
        StubHooks();
        var parentLog = StageParentLog(WorkerSpawnLine, WorkerBoundLine);

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SubagentStart","subagent_id":"{{AnnouncedSubagentId}}","child_session_id":"{{AnnouncedChild}}","session_id":"{{AnnouncedChild}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        var posts = HookPosts("/hooks/subagent-start");
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonDocument.Parse(posts[0]).RootElement;
        await Assert.That(body.GetProperty("agent_type").GetString()).IsEqualTo("Test coverage auditor — Integrations group");
        await Assert.That(body.GetProperty("agent_id").GetString()).IsEqualTo("01a13565a6267f70bda01c19ba4deadf");
        await Assert.That(body.GetProperty("transcript_path").GetString())
            .IsEqualTo(MusePaths.ChildSessionLogPath(parentLog, AnnouncedChild));
    }

    /// <summary>The fragment is the only thing SessionStart writes, held under Muse's stdout cap with
    /// the marker first; an index well past the cap proves the cap, not the emitter, bounds it.</summary>
    [Test, NotInParallel("AuthProviderDiscoveryCache")]
    public async Task SessionStart_writes_only_the_capped_memory_fragment() {
        StubHooks();
        StubMemoryIndex(200);
        StageParentLog();
        var stdout = new StringWriter();

        var exit = await Command().Handle(new StringReader(SessionStartPayload("startup")), stdout, _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        var written = stdout.ToString();
        await Assert.That(written.StartsWith("<!-- kcap-memory-index:v1 -->\n", StringComparison.Ordinal)).IsTrue();
        await Assert.That(written).Contains("## Team memory");
        await Assert.That(written.EndsWith('\n')).IsTrue();
        await Assert.That(Encoding.UTF8.GetByteCount(written)).IsLessThanOrEqualTo(MuseHookCommand.StdoutCapBytes);
        await Assert.That(Encoding.UTF8.GetByteCount(written)).IsGreaterThan(MuseHookCommand.StdoutCapBytes - 1024);
    }

    /// <summary>Muse kills the hook at 5 s, well inside the POST's default 30 s of retries. The memory
    /// route answers, so a missing fragment could only mean the stalled POST kept the hook from writing.</summary>
    [Test, NotInParallel("AuthProviderDiscoveryCache")]
    public async Task SessionStart_against_a_stalled_server_spools_the_start_and_still_writes_the_fragment() {
        _server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(10)));
        StubMemoryIndex(3);
        StageParentLog();
        var stdout  = new StringWriter();
        await WarmServer();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        var exit = await Command(time: TimeProvider.System).Handle(new StringReader(SessionStartPayload("startup")), stdout, _ => null, Today);

        elapsed.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        await Assert.That(new HookSpool(Config.Root, TimeProvider.System).HasBacklog(ParentDashless)).IsTrue();
        await Assert.That(stdout.ToString().StartsWith("<!-- kcap-memory-index:v1 -->\n", StringComparison.Ordinal)).IsTrue();
    }

    // ── What a hosted session and a flow participant are given ─────────────────────────────

    async Task<string> StartWithEveryFragmentAvailableAsync(string? sessionKind, bool hosted) {
        StubHooks();
        StubMemoryIndex(3);
        StageParentLog();
        Home.CreateFile(Path.GetRelativePath(Home.Path, TestHarnesses.Under(Home).Of<MuseHarness>().Paths.SettingsJson),
            """{"mcp_servers":{"kcap-workitems":{"command":"kcap"},"kcap-plans":{"command":"kcap"},"kcap-flows":{"command":"kcap"}}}""");
        _server.Given(Request.Create().WithPath("/api/flows/definitions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"definitions":[{"id":"code-review","offer":"proactive","when_to_use":"After a change is complete."}]}"""));
        new FirstRunNoticeStore(Config.Root).Arm();
        if (hosted)
            MuseHostedSessionMarker.Write(Home, new MuseHostedSessionMarker(
                ParentId, "agent-from-marker", "daemon-1", "epoch-1", DateTimeOffset.UtcNow, SessionKind: sessionKind));

        var stdout = new StringWriter();
        var exit   = await Command().Handle(new StringReader(SessionStartPayload("startup")), stdout, _ => null, Today);

        await Assert.That(exit).IsEqualTo(0);
        return stdout.ToString();
    }

    /// <summary>The control the two tests below are measured against.</summary>
    [Test]
    public async Task An_interactive_session_gets_every_fragment() {
        var written = await StartWithEveryFragmentAvailableAsync(sessionKind: null, hosted: false);

        await Assert.That(written).Contains("## Team memory");
        await Assert.That(written).Contains("## Work items");
        await Assert.That(written).Contains("## Plans");
        await Assert.That(written).Contains("Flows you may offer");
        await Assert.That(written).Contains("## kcap is set up");
    }

    /// <summary>Muse runs a hook with a cleared environment, so the daemon's marker file is the only
    /// place the participant kind can come from.</summary>
    [Test]
    public async Task A_flow_participant_named_by_the_marker_gets_no_driver_guidance_or_onboarding() {
        var written = await StartWithEveryFragmentAvailableAsync(HostedSessionKinds.FlowParticipantWire, hosted: true);

        await Assert.That(written).DoesNotContain("## Team memory");
        await Assert.That(written).DoesNotContain("## Work items");
        await Assert.That(written).DoesNotContain("## Plans");
        await Assert.That(written).DoesNotContain("kcap session id:");
        await Assert.That(written).DoesNotContain("## kcap is set up");
        await Assert.That(written).DoesNotContain("Flows you may offer");
    }

    /// <summary>A marker an older daemon wrote carries no kind: the session is hosted, not a participant.</summary>
    [Test]
    [Arguments(HostedSessionKinds.HostedAgentWire)]
    [Arguments(null)]
    public async Task A_hosted_agent_keeps_the_driver_guidance_and_loses_the_onboarding(string? sessionKind) {
        var written = await StartWithEveryFragmentAvailableAsync(sessionKind, hosted: true);

        await Assert.That(written).Contains("## Team memory");
        await Assert.That(written).Contains("## Work items");
        await Assert.That(written).Contains("## Plans");
        await Assert.That(written).DoesNotContain("## kcap is set up");
        await Assert.That(new FirstRunNoticeStore(Config.Root).TryClaim()).IsTrue();
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

    /// <summary>Muse gives a SessionEnd hook far less than its documented budget under <c>muse exec</c>,
    /// so the end is handed to the detached watcher: the hook writes the request and posts nothing,
    /// and the watcher it would otherwise have killed stays alive to drain and post it.</summary>
    [Test]
    public async Task SessionEnd_with_a_live_watcher_hands_the_end_to_the_watcher_and_posts_nothing() {
        StubHooks();
        StageParentLog();
        MuseHostedSessionMarker.Write(Home,
            new MuseHostedSessionMarker(ParentId, "agent-from-marker", "daemon-1", "epoch-1", DateTimeOffset.UtcNow));
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Directory.CreateDirectory(paths.Directory);
        await File.WriteAllTextAsync(paths.PidFile(ParentDashless), $"{Environment.ProcessId}\nown-token\n");

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SessionEnd","reason":"exit","session_id":"{{ParentId}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(HookPosts("/hooks/").Count).IsEqualTo(0);
        await Assert.That(File.Exists(paths.PidFile(ParentDashless))).IsTrue();
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(0);

        var request = MuseSessionEndRequest.TryClaim(paths, ParentDashless, "test");
        await Assert.That(request).IsNotNull();
        var body = JsonDocument.Parse(request!.Payload).RootElement;
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(ParentDashless);
        await Assert.That(body.GetProperty("reason").GetString()).IsEqualTo("exit");
        await Assert.That(body.GetProperty("agent_host_id").GetString()).IsEqualTo("agent-from-marker");
        await Assert.That(body.TryGetProperty("ended_at", out _)).IsTrue();
        await Assert.That(MuseHostedSessionMarker.TryRead(Home, ParentId)).IsNull();
    }

    /// <summary>A session whose watcher is gone still has a log to drain, so the end is handed to a watcher
    /// armed for it rather than posted from a hook Muse may kill mid-drain.</summary>
    [Test]
    public async Task SessionEnd_without_a_watcher_arms_one_for_the_handed_end_and_posts_nothing() {
        StubHooks();
        var log = StageParentLog();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SessionEnd","reason":"exit","session_id":"{{ParentId}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(HookPosts("/hooks/").Count).IsEqualTo(0);
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
        await Assert.That(_spawner.Spawned[0].TranscriptPath).IsEqualTo(log);
        await Assert.That(_spawner.Spawned[0].Vendor).IsEqualTo("muse");

        var request = MuseSessionEndRequest.TryClaim(WatcherPaths.FromEnvironment(Config.Root), ParentDashless, "test");
        await Assert.That(request).IsNotNull();
        await Assert.That(JsonDocument.Parse(request!.Payload).RootElement.GetProperty("reason").GetString()).IsEqualTo("exit");
    }

    /// <summary>A session Stop re-arms after an unposted end was left behind: the end is discarded through
    /// the same claim a watcher would take, so it cannot end the new watcher.</summary>
    [Test]
    public async Task Stop_rearm_discards_an_unclaimed_handed_end() {
        StubHooks();
        StageParentLog();
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        MuseSessionEndRequest.Write(paths, ParentDashless, """{"reason":"stale"}""");

        await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"done","session_id":"{{ParentId}}","turn_id":"3a75e7fd-2b07-401b-9446-6af740e7933d","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(MuseSessionEndRequest.Exists(paths, ParentDashless)).IsFalse();
        await Assert.That(MuseSessionEndRequest.ResumedSince(paths, ParentDashless, DateTimeOffset.UtcNow.AddMinutes(-1))).IsTrue();
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
    }

    /// <summary>An end handed over after the session was marked resumed is the resumed run's own; the hook
    /// must leave it for the watcher it arms. The request is stamped ahead of the mark the hook writes.</summary>
    [Test]
    public async Task Stop_rearm_keeps_a_handed_end_newer_than_the_resume() {
        StubHooks();
        StageParentLog();
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        MuseSessionEndRequest.Write(paths, ParentDashless, """{"reason":"fresh"}""");
        File.SetLastWriteTimeUtc(MuseSessionEndRequest.PathFor(paths, ParentDashless), DateTime.UtcNow.AddMinutes(2));

        await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"Stop","stop_hook_active":false,"last_assistant_message":"done","session_id":"{{ParentId}}","turn_id":"3a75e7fd-2b07-401b-9446-6af740e7933d","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            new StringWriter(), ParentEnv, Today);

        await Assert.That(MuseSessionEndRequest.Exists(paths, ParentDashless)).IsTrue();
        await Assert.That(MuseSessionEndRequest.TryClaim(paths, ParentDashless, "test")?.Payload).IsEqualTo("""{"reason":"fresh"}""");
        await Assert.That(_spawner.Spawned.Count).IsEqualTo(1);
    }

    /// <summary>With no log there is nothing to drain, so the end goes straight to the server.</summary>
    [Test]
    public async Task SessionEnd_posts_session_end_with_the_reason() {
        StubHooks();
        var stdout = new StringWriter();

        var exit = await Command().Handle(new StringReader(
            $$"""{"hook_event_name":"SessionEnd","reason":"other","session_id":"{{ParentId}}","cwd":"{{AbsentCwd()}}","transcript_path":null}"""),
            stdout, ParentEnv, Today);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.ToString()).IsEmpty();
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

    MuseHookCommand Command(
            FakeWatcherSpawner? spawner = null, HarnessRegistry? harnesses = null, ConfigRoot? root = null, ProfileContext? profiles = null,
            TimeProvider? time = null) {
        root     ??= Config.Root;
        profiles ??= Resolutions.At(_server.Url!, root);
        var http   = new FixedCapacitorHttpClient();

        // Payload and ordering assertions should not depend on CPU scheduling. Only the
        // elapsed-time/spooling test above supplies the real clock.
        return new MuseHookCommand(root, profiles, new HookClock(time ?? new FakeTimeProvider(DateTimeOffset.UtcNow)), Home, harnesses ?? TestHarnesses.Under(Home),
            HostedAgent.Terminal, http, TestWatchers.For(root, profiles, http, spawner ?? _spawner), new GitProviderRouter(),
            new WorkingDirectory(AppContext.BaseDirectory));
    }

    /// <summary>A config root of its own whose active profile points at <paramref name="serverUrl"/>.</summary>
    string DaemonRoot(string serverUrl, string? teamBUrl = null) {
        WriteRootConfig(Tmp.PathTo("daemon-config"), serverUrl, teamBUrl);
        return Tmp.PathTo("daemon-config");
    }

    /// <summary>The config the home-derived root holds: what a cleared-environment hook resolves by itself.</summary>
    void HomeRoot(string serverUrl) => WriteRootConfig(ConfigRoot.UnderHome(Home.Path).Directory, serverUrl, null);

    static void WriteRootConfig(string dir, string activeUrl, string? teamBUrl) {
        Directory.CreateDirectory(dir);
        var teamB = teamBUrl is null ? "" : $$""", "team-b": { "server_url": "{{teamBUrl}}" }""";
        File.WriteAllText(Path.Join(dir, "config.json"), $$"""
            {
              "version": 2,
              "active_profile": "default",
              "profiles": { "default": { "server_url": "{{activeUrl}}" }{{teamB}} },
              "profile_bindings": {}
            }
            """);
    }

    void WriteMarker(string? configDir, string? serverUrl, string? profile = null) =>
        MuseHostedSessionMarker.Write(Home, new MuseHostedSessionMarker(
            ParentId, "agent-from-marker", "daemon-1", "epoch-1", DateTimeOffset.UtcNow, configDir, serverUrl, profile));

    /// <summary>The process-level resolution a cleared-environment hook gets, as the composition root builds it.</summary>
    static async Task<(ConfigRoot Root, ProfileContext Profiles)> Resolve(MuseHookConnection connection, Func<ConfigRoot> fallback) {
        var root = connection.ConfigRootOr(fallback);
        return (root, connection.Bind(await AppConfig.ResolveActiveProfile([], root, connection.Apply(ProfileOverrides.None), NoLocalInstallation.Endpoints)));
    }

    string StageParentLog(params string[] lines) {
        var log = new MusePaths(Home, null, null).SessionLogPath(ParentId, Today)!;

        return Home.CreateFile(Path.GetRelativePath(Home.Path, log), lines.Length == 0 ? "{}\n" : string.Join("\n", lines) + "\n");
    }

    void StubMemoryIndex(int entries) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("""{"provider":"None"}"""));

        var rows = string.Join(",", Enumerable.Range(0, entries).Select(i =>
            $$"""{"memory_id":"m{{i}}","slug":"memory-{{i}}","audience":"org","description":"{{new string('d', 400)}}","kind":"preference"}"""));
        _server.Given(Request.Create().WithPath("/api/memories/index").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json").WithBody("[" + rows + "]"));
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

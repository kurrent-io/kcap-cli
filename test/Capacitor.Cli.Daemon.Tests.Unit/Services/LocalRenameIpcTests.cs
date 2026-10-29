using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The SessionRename handler: every outcome is one SessionRenameAck, each refusal is decided
/// before the server is asked, and only a rename the server applied reaches the status title.
/// </summary>
public class LocalRenameIpcTests {
    const string Subject = "user_01";

    sealed class FakeTitleServer : ITitleServerPort {
        public TitleRenameOutcome Outcome { get; set; } = new(true, null, null);
        public Exception? RenameThrows { get; set; }
        public string? ServerTitle { get; set; }
        public List<(string SessionId, string Title)> Renames { get; } = [];

        public Task<string?> GetTitleAsync(string sessionId, CancellationToken ct) => Task.FromResult(ServerTitle);
        public Task<HarnessTitleOutcome> PushTitleAsync(string sessionId, HarnessTitlePost post, CancellationToken ct) =>
            Task.FromResult(HarnessTitleOutcome.Failed);

        public Task<TitleRenameOutcome> RenameAsync(string sessionId, string title, CancellationToken ct) {
            Renames.Add((sessionId, title));
            return RenameThrows is { } ex ? Task.FromException<TitleRenameOutcome>(ex) : Task.FromResult(Outcome);
        }
    }

    static (AgentOrchestrator Orch, FakeTitleServer Server) Build() {
        var (orch, server, _) = BuildWithConnection();
        return (orch, server);
    }

    static (AgentOrchestrator Orch, FakeTitleServer Server, CaptureServerConnection Connection) BuildWithConnection() {
        var connection = new CaptureServerConnection();
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            connection, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var server = new FakeTitleServer();
        orch.TitleServerForTest = server;
        orch.SetUserSubjectForTest(Subject);

        return (orch, server, connection);
    }

    static async Task<SessionRenameAckDto> Rename(AgentOrchestrator orch, string payload) {
        using var ms = new MemoryStream();
        await orch.HandleLocalRenameAsync(payload, ms, CancellationToken.None);
        ms.Position = 0;
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.SessionRenameAck);
        await Assert.That(await FrameCodec.ReadAsync(ms, CancellationToken.None)).IsNull();

        return JsonSerializer.Deserialize(frame.Text, RenameIpcJsonContext.Default.SessionRenameAckDto)!;
    }

    static string Payload(string agentId, string title, string? subject = Subject) =>
        JsonSerializer.Serialize(new SessionRenameDto(agentId, title, subject), RenameIpcJsonContext.Default.SessionRenameDto);

    static string? StatusTitle(AgentOrchestrator orch, string agentId) =>
        orch.SnapshotAgentsForStatus().Single(a => a.Id == agentId).Title;

    [Test]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("""{"agent_id":"a1"}""")]
    [Arguments("""{"agent_id":"a1","title":"   "}""")]
    [Arguments("""{"agent_id":null,"title":"x"}""")]
    public async Task Malformed_payloads_ack_malformed(string payload) {
        var (orch, server) = Build();
        await using var _ = orch;

        var ack = await Rename(orch, payload);

        await Assert.That(ack).IsEqualTo(new SessionRenameAckDto(false, SessionRenameReasons.Malformed, null));
        await Assert.That(server.Renames).IsEmpty();
    }

    [Test]
    public async Task Unknown_agent_is_refused() {
        var (orch, server) = Build();
        await using var _ = orch;

        await Assert.That((await Rename(orch, Payload("nope", "New"))).Reason).IsEqualTo(SessionRenameReasons.UnknownAgent);
        await Assert.That(server.Renames).IsEmpty();
    }

    [Test]
    public async Task Private_agent_is_refused() {
        var (orch, server) = Build();
        await using var _ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "prv", new FakeAcpRuntime(), isPrivate: true);

        await Assert.That((await Rename(orch, Payload("prv", "New"))).Reason).IsEqualTo(SessionRenameReasons.PrivateAgent);
        await Assert.That(server.Renames).IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments("someone_else")]
    public async Task A_missing_or_different_subject_is_an_identity_mismatch(string? subject) {
        var (orch, server) = Build();
        await using var _ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());

        await Assert.That((await Rename(orch, Payload("a1", "New", subject))).Reason).IsEqualTo(SessionRenameReasons.IdentityMismatch);
        await Assert.That(server.Renames).IsEmpty();
    }

    [Test]
    public async Task A_signed_out_daemon_is_an_identity_mismatch() {
        var (orch, server) = Build();
        await using var _ = orch;
        orch.SetUserSubjectForTest(null);
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());

        await Assert.That((await Rename(orch, Payload("a1", "New"))).Reason).IsEqualTo(SessionRenameReasons.IdentityMismatch);
        await Assert.That(server.Renames).IsEmpty();
    }

    [Test]
    public async Task An_agent_without_a_session_is_refused() {
        var (orch, server) = Build();
        await using var _ = orch;
        orch.SeedAgentForTest("pty");

        await Assert.That((await Rename(orch, Payload("pty", "New"))).Reason).IsEqualTo(SessionRenameReasons.NoSession);
        await Assert.That(server.Renames).IsEmpty();
    }

    [Test]
    public async Task Applied_rename_updates_the_status_title() {
        var (orch, server) = Build();
        await using var _ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime { AcpSessionId = "sess-9" });
        server.Outcome = new TitleRenameOutcome(true, "Shown", null);

        var ack = await Rename(orch, Payload("a1", "  Asked  "));

        await Assert.That(ack).IsEqualTo(new SessionRenameAckDto(true, null, "Shown"));
        await Assert.That(server.Renames).IsEquivalentTo(new[] { ("sess-9", "Asked") });
        await Assert.That(StatusTitle(orch, "a1")).IsEqualTo("Shown");
    }

    [Test]
    public async Task An_applied_rename_without_a_shown_title_shows_the_asked_one() {
        var (orch, server) = Build();
        await using var _ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());
        server.Outcome = new TitleRenameOutcome(true, null, null);

        var ack = await Rename(orch, Payload("a1", "Asked"));

        await Assert.That(ack).IsEqualTo(new SessionRenameAckDto(true, null, "Asked"));
        await Assert.That(StatusTitle(orch, "a1")).IsEqualTo("Asked");
    }

    /// The server's title is a lagging read model: a tick that still reads the pre-rename title
    /// must not revert what the rename applied.
    [Test]
    public async Task An_applied_rename_survives_a_tick_whose_server_read_lags() {
        var (orch, server) = Build();
        await using var _ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());
        var loop = orch.PublishTitleLoopForTest();
        server.ServerTitle = "Old";

        server.Outcome = new TitleRenameOutcome(true, "New", null);
        await Assert.That((await Rename(orch, Payload("a1", "New"))).Ok).IsTrue();
        await Assert.That(StatusTitle(orch, "a1")).IsEqualTo("New");

        await loop.TickAsync(CancellationToken.None);

        await Assert.That(StatusTitle(orch, "a1")).IsEqualTo("New");
    }

    [Test]
    public async Task A_failing_server_call_is_a_server_error_and_leaves_the_title() {
        var (orch, server) = Build();
        await using var _ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());
        var before = StatusTitle(orch, "a1");
        server.RenameThrows = new HttpRequestException("boom");

        var ack = await Rename(orch, Payload("a1", "Asked"));

        await Assert.That(ack).IsEqualTo(new SessionRenameAckDto(false, SessionRenameReasons.ServerError, null));
        await Assert.That(StatusTitle(orch, "a1")).IsEqualTo(before);
    }

    [Test]
    public async Task Refused_rename_leaves_the_title() {
        var (orch, server) = Build();
        await using var _ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", new FakeAcpRuntime());
        var before = StatusTitle(orch, "a1");
        server.Outcome = new TitleRenameOutcome(false, "Kept", SessionRenameReasons.NotOwned);

        var ack = await Rename(orch, Payload("a1", "Asked"));

        await Assert.That(ack).IsEqualTo(new SessionRenameAckDto(false, SessionRenameReasons.NotOwned, "Kept"));
        await Assert.That(StatusTitle(orch, "a1")).IsEqualTo(before);
    }

    [Test]
    public async Task Status_rows_carry_is_private() {
        var (orch, _) = Build();
        await using var __ = orch;
        AgentOrchestratorHarness.SeedAcpAgent(orch, "prv", new FakeAcpRuntime(), isPrivate: true);
        AgentOrchestratorHarness.SeedAcpAgent(orch, "pub", new FakeAcpRuntime());

        var rows = orch.SnapshotAgentsForStatus();

        await Assert.That(rows.Single(a => a.Id == "prv").IsPrivate).IsTrue();
        await Assert.That(rows.Single(a => a.Id == "pub").IsPrivate).IsFalse();
    }

    [Test]
    public async Task Status_daemon_info_carries_the_cached_user_subject() {
        var (orch, _) = Build();
        await using var __ = orch;
        var config = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1" };
        var ipc    = new DaemonStatusIpc(config, orch, new CaptureServerConnection(), new DaemonStatusNotifier(), TimeProvider.System);

        using var signedIn = JsonDocument.Parse(ipc.Snapshot());
        await Assert.That(signedIn.RootElement.GetProperty("daemon").GetProperty("user_subject").GetString()).IsEqualTo(Subject);

        orch.SetUserSubjectForTest(null);
        using var signedOut = JsonDocument.Parse(ipc.Snapshot());
        await Assert.That(signedOut.RootElement.GetProperty("daemon").GetProperty("user_subject").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task A_token_subject_is_read_from_the_access_token() {
        await Assert.That(AgentOrchestrator.SubjectOf(Jwt("""{"sub":"user_42"}"""))).IsEqualTo("user_42");
        await Assert.That(AgentOrchestrator.SubjectOf(null)).IsNull();
        await Assert.That(AgentOrchestrator.SubjectOf("not-a-jwt")).IsNull();
    }

    /// Every credential the connection's lanes resolve feeds the cached subject, so a re-login shows on the next read.
    [Test]
    public async Task A_credential_resolved_on_the_connection_updates_the_subject() {
        var (orch, _, connection) = BuildWithConnection();
        await using var __ = orch;

        connection.OnCredentialResolved!(Credential(AuthStatus.Ok, Jwt("""{"sub":"user_new"}""")));
        await Assert.That(orch.UserSubject).IsEqualTo("user_new");

        connection.OnCredentialResolved!(Credential(AuthStatus.Expired, null));
        await Assert.That(orch.UserSubject).IsEqualTo("user_new");

        connection.OnCredentialResolved!(Credential(AuthStatus.NotAuthenticated, null));
        await Assert.That(orch.UserSubject).IsNull();
    }

    static CredentialState Credential(AuthStatus status, string? accessToken) => new(
        accessToken, status,
        new TokenResolution(
            accessToken is null ? null : new StoredTokens { AccessToken = accessToken, ExpiresAt = DateTimeOffset.MaxValue, GitHubUsername = "u" },
            status, null, "default"));

    static string Jwt(string payload) {
        static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"none"}""")}.{B64(payload)}.sig";
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Pins the server-facing contract of the session-mutating commands — <c>kcap disable</c>,
/// <c>kcap hide</c>, <c>kcap set-title</c> — entirely from outside the process: the method, path,
/// body and headers a stub server receives, plus the child's stdout, stderr and exit code.
///
/// <para>Nothing here names a type the CLI builds its client from, so the same assertions hold
/// however that construction is wired internally.</para>
/// </summary>
public class SessionMutationServerContractTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    const string SessionId     = "11111111-2222-3333-4444-555555555555";
    const string SessionIdPath = "11111111222233334444555555555555";
    const string BearerToken   = "seed-token";

    readonly WireMockServer _server   = WireMockServer.Start();
    readonly List<Process>  _children = [];

    public void Dispose() {
        foreach (var child in _children) {
            try {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            } catch {
                // best-effort cleanup
            }

            child.Dispose();
        }

        _server.Stop();
    }

    /// <summary>Runs before every test — TUnit injects the temp directories before its hooks.</summary>
    [Before(Test)]
    public void SeedProfileAndToken() {
        Config.CreateFile("config.json", ProfileJson);
        Config.CreateDir("tokens").CreateFile("default.json", TokenJson);
    }

    // --- kcap disable ---

    /// <summary>Success deletes the server's copy and says so, with the token and version headers attached.</summary>
    [Test]
    public async Task Disable_deletes_the_server_side_session() {
        StubAuthConfig();
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionIdPath}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var run = await RunAsync(_server.Url!, sessionIdEnv: null, "disable", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains($"Session {SessionIdPath} disabled.");
        await Assert.That(run.Stdout).Contains("Recording stopped and server data deleted.");

        var deletes = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionIdPath}").UsingDelete());
        await Assert.That(deletes.Count).IsEqualTo(1);
        await Assert.That(Header(deletes[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
        await Assert.That(Header(deletes[0], "X-Kcap-Cli-Version")).IsNotNull();
    }

    /// <summary>A 404 is an outcome, not a failure: local state still changed, so the exit code stays 0.</summary>
    [Test]
    public async Task Disable_reports_absent_server_data_on_404() {
        StubAuthConfig();
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionIdPath}").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(404));

        var run = await RunAsync(_server.Url!, sessionIdEnv: null, "disable", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("No server data found");
    }

    /// <summary>
    /// A server that never answered leaves the local half of the command done, so the diagnostic goes
    /// to stderr and the command still succeeds. Costs the full 30s transport retry budget.
    /// </summary>
    [Test]
    public async Task Disable_succeeds_locally_when_the_server_is_unreachable() {
        var run = await RunAsync(UnreachableUrl, sessionIdEnv: null, "disable", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stderr).Contains("kcap API cannot be reached");
        await Assert.That(run.Stdout).Contains("Session disabled locally");
    }

    // --- kcap hide ---

    /// <summary>Hiding is a visibility PUT carrying exactly <c>{"visibility":"none"}</c>.</summary>
    [Test]
    public async Task Hide_puts_owner_only_visibility() {
        StubAuthConfig();
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionIdPath}/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        var run = await RunAsync(_server.Url!, sessionIdEnv: null, "hide", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains($"Session {SessionIdPath} hidden (owner-only).");

        var puts = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionIdPath}/visibility").UsingPut());
        await Assert.That(puts.Count).IsEqualTo(1);

        var body = JsonNode.Parse(puts[0].RequestMessage.Body!)!;
        await Assert.That(body["visibility"]?.GetValue<string>()).IsEqualTo("none");
        await Assert.That(Header(puts[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
        await Assert.That(Header(puts[0], "X-Kcap-Cli-Version")).IsNotNull();
    }

    /// <summary>A refused change is a failure — nothing local compensates for it.</summary>
    [Test]
    public async Task Hide_fails_when_the_server_refuses() {
        StubAuthConfig();
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionIdPath}/visibility").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(500));

        var run = await RunAsync(_server.Url!, sessionIdEnv: null, "hide", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains("Server returned HTTP 500");
        await Assert.That(run.Stdout).DoesNotContain("hidden (owner-only)");
    }

    // --- kcap set-title ---

    void StubAgentTitle(bool applied, string? shown) =>
        _server.Given(Request.Create().WithPath("/hooks/agent-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody(new JsonObject { ["applied"] = applied, ["title"] = shown }.ToJsonString()));

    JsonNode SingleAgentTitleBody() {
        var posts = _server.FindLogEntries(Request.Create().WithPath("/hooks/agent-title").UsingPost());

        return posts.Count == 1 ? JsonNode.Parse(posts[0].RequestMessage.Body!)! : throw new InvalidOperationException($"{posts.Count} agent-title posts");
    }

    /// <summary>A plain title is a rename: the post carries the ambient session id and the joined title, and an applied
    /// title prints nothing.</summary>
    [Test]
    public async Task Set_title_posts_a_rename_with_the_session_id_and_title() {
        StubAuthConfig();
        StubAgentTitle(applied: true, "Refit the boiler");

        var run = await RunAsync(_server.Url!, sessionIdEnv: SessionId, "set-title", "Refit", "the", "boiler");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).IsEqualTo("");
        await Assert.That(run.Stderr).IsEqualTo("");

        var posts = _server.FindLogEntries(Request.Create().WithPath("/hooks/agent-title").UsingPost());
        var body  = SingleAgentTitleBody();
        await Assert.That(body["session_id"]?.GetValue<string>()).IsEqualTo(SessionIdPath);
        await Assert.That(body["title"]?.GetValue<string>()).IsEqualTo("Refit the boiler");
        await Assert.That(body["rename"]?.GetValue<bool>()).IsTrue();
        await Assert.That(Header(posts[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
        await Assert.That(Header(posts[0], "X-Kcap-Cli-Version")).IsNotNull();
    }

    [Test]
    public async Task Set_title_auto_posts_a_title_that_is_not_a_rename() {
        StubAuthConfig();
        StubAgentTitle(applied: true, "Refit the boiler");

        var run = await RunAsync(_server.Url!, sessionIdEnv: SessionId, "set-title", "--auto", "Refit the boiler");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        var body = SingleAgentTitleBody();
        await Assert.That(body["title"]?.GetValue<string>()).IsEqualTo("Refit the boiler");
        await Assert.That(body["rename"]?.GetValue<bool>()).IsFalse();
    }

    /// <summary>The server answering that another title stands is not success: the agent must not report a rename.</summary>
    [Test]
    public async Task Set_title_reports_a_rename_the_server_did_not_apply() {
        StubAuthConfig();
        StubAgentTitle(applied: false, "Renamed in the dashboard");

        var run = await RunAsync(_server.Url!, sessionIdEnv: SessionId, "set-title", "Refit");

        await Assert.That(run.ExitCode).IsEqualTo(3);
        await Assert.That(run.Stderr).Contains("Title not applied: the session keeps \"Renamed in the dashboard\".");
    }

    /// <summary>A chosen title outranking the first-prompt title is the intended outcome, so it is reported without
    /// failing the command the hook asks the agent to run silently.</summary>
    [Test]
    public async Task Set_title_auto_that_loses_says_so_and_succeeds() {
        StubAuthConfig();
        StubAgentTitle(applied: false, shown: null);

        var run = await RunAsync(_server.Url!, sessionIdEnv: SessionId, "set-title", "--auto", "Refit");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stderr).Contains("Title not applied: the session keeps its current title.");
    }

    [Test]
    public async Task Set_title_reads_auto_later_in_the_title_as_a_word() {
        StubAuthConfig();
        StubAgentTitle(applied: true, "Explain the --auto flag");

        var run = await RunAsync(_server.Url!, sessionIdEnv: SessionId, "set-title", "Explain", "the", "--auto", "flag");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        var body = SingleAgentTitleBody();
        await Assert.That(body["title"]?.GetValue<string>()).IsEqualTo("Explain the --auto flag");
        await Assert.That(body["rename"]?.GetValue<bool>()).IsTrue();
    }

    /// <summary>An older server has no agent-title route (a bare 404): the title goes to set-title, which only fills
    /// an untitled session, so the command cannot claim it applied.</summary>
    [Test]
    public async Task Set_title_against_an_older_server_falls_back_and_says_it_is_unconfirmed() {
        StubAuthConfig();
        _server.Given(Request.Create().WithPath("/hooks/agent-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        var run = await RunAsync(_server.Url!, sessionIdEnv: SessionId, "set-title", "Refit");

        await Assert.That(run.ExitCode).IsEqualTo(3);
        await Assert.That(run.Stderr).Contains("cannot confirm");

        var posts = _server.FindLogEntries(Request.Create().WithPath("/hooks/set-title").UsingPost());
        await Assert.That(posts.Count).IsEqualTo(1);
        await Assert.That(JsonNode.Parse(posts[0].RequestMessage.Body!)!["title"]?.GetValue<string>()).IsEqualTo("Refit");
    }

    /// <summary>A rejected title is a plain failure: the status is reported and the exit code is 1.</summary>
    [Test]
    public async Task Set_title_fails_when_the_server_refuses() {
        StubAuthConfig();
        _server.Given(Request.Create().WithPath("/hooks/agent-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403));

        var run = await RunAsync(_server.Url!, sessionIdEnv: SessionId, "set-title", "Refit");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains("Server returned HTTP 403");
    }

    // --- fixtures ---

    /// <summary>No listener has ever bound port 1, so every attempt is refused at once.</summary>
    const string UnreachableUrl = "http://127.0.0.1:1";

    /// <summary><c>update_check: false</c> keeps the exit-time update notice — and its network
    /// probe — out of the child's stderr.</summary>
    const string ProfileJson =
        """{"version":2,"active_profile":"default","profiles":{"default":{"update_check":false}},"profile_bindings":{},"cwd_remap":[]}""";

    static string TokenJson =>
        $$"""
        {"access_token":"{{BearerToken}}","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}",
         "github_username":"seed-user","provider":"GitHubApp"}
        """;

    void StubAuthConfig() =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"GitHubApp"}"""));

    static string? Header(ILogEntry entry, string name) {
        if (entry.RequestMessage.Headers is not { } headers) return null;

        foreach (var header in headers) {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) {
                return string.Join(',', header.Value);
            }
        }

        return null;
    }

    async Task<CliRun> RunAsync(string url, string? sessionIdEnv, params string[] args) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, args);
        psi.WorkingDirectory                = Tmp.Path;
        psi.Environment["KCAP_URL"]         = url;
        psi.Environment["KCAP_SESSION_ID"]  = sessionIdEnv ?? "";
        psi.Environment["CODEX_THREAD_ID"]  = "";
        psi.Environment["DO_NOT_TRACK"]     = "1";

        var child = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _children.Add(child);
        child.StandardInput.Close();

        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await child.WaitForExitAsync(cts.Token);

        return new(child.ExitCode, await stdout, await stderr);
    }

    readonly record struct CliRun(int ExitCode, string Stdout, string Stderr);
}

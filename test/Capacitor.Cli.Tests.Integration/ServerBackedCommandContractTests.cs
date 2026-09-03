using System.Diagnostics;
using System.Text.Json.Nodes;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Pins the server-facing contract of the read-and-report commands — <c>kcap errors</c>,
/// <c>kcap projects</c>, <c>kcap recap</c>, <c>kcap validate-plan</c>, <c>kcap feedback</c> — from
/// outside the process: the method, path, query, body and headers a stub server receives, and the
/// stdout, stderr and exit code the child answers with. One success shape and one failure shape
/// each.
/// </summary>
public class ServerBackedCommandContractTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    const string SessionId   = "sess-characterization";
    const string BearerToken = "seed-token";

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
        StubAuthConfig();
    }

    // --- kcap errors ---

    /// <summary>The plain form reads the session's error list with no chain widening, and renders one line per error.</summary>
    [Test]
    public async Task Errors_reads_the_session_error_list() {
        const string body = """
            [{"session_id":"sess-characterization","session_slug":"tidy-otter","agent_id":null,
              "event_number":7,"tool_name":"Bash","error":"exit status 2",
              "timestamp":"2026-01-02T03:04:05+00:00"}]
            """;

        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/errors").UsingGet())
            .RespondWith(Json(200, body));

        var run = await RunAsync("errors", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("Found 1 error(s):");
        await Assert.That(run.Stdout).Contains("[tidy-otter] #7 Bash");
        await Assert.That(run.Stdout).Contains("exit status 2");

        var gets = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionId}/errors").UsingGet());
        await Assert.That(gets.Count).IsEqualTo(1);
        await Assert.That(Header(gets[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
        await Assert.That(Header(gets[0], "X-Kcap-Cli-Version")).IsNotNull();

        await Assert.That(gets[0].RequestMessage.RawQuery ?? "").IsEqualTo("");
    }

    /// <summary><c>--chain</c> is carried as a query parameter, not a different route.</summary>
    [Test]
    public async Task Errors_chain_widens_the_request_with_a_query_parameter() {
        _server.Given(
                Request.Create().WithPath($"/api/sessions/{SessionId}/errors")
                    .WithParam("chain", "true").UsingGet())
            .RespondWith(Json(200, "[]"));

        var run = await RunAsync("errors", "--chain", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("No errors found.");

        var gets = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionId}/errors").UsingGet());
        await Assert.That(gets.Count).IsEqualTo(1);
        await Assert.That(gets[0].RequestMessage.RawQuery).IsEqualTo("?chain=true");
    }

    /// <summary>A 404 names the session that was not found and fails.</summary>
    [Test]
    public async Task Errors_reports_an_unknown_session() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/errors").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var run = await RunAsync("errors", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains($"Session not found: {SessionId}");
    }

    // --- kcap projects ---

    /// <summary>An empty project list is a success, and the request still carries the token and version headers.</summary>
    [Test]
    public async Task Projects_reads_the_project_list() {
        _server.Given(Request.Create().WithPath("/api/projects").UsingGet())
            .RespondWith(Json(200, "[]"));

        var run = await RunAsync("projects");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("No projects found.");

        var gets = _server.FindLogEntries(Request.Create().WithPath("/api/projects").UsingGet());
        await Assert.That(gets.Count).IsEqualTo(1);
        await Assert.That(Header(gets[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
        await Assert.That(Header(gets[0], "X-Kcap-Cli-Version")).IsNotNull();
    }

    /// <summary>The coded plan refusal becomes plan advice rather than a bare "Forbidden".</summary>
    [Test]
    public async Task Projects_translates_the_plan_gate() {
        _server.Given(Request.Create().WithPath("/api/projects").UsingGet())
            .RespondWith(Json(403, """{"error":"projects_not_in_plan"}"""));

        var run = await RunAsync("projects");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains("Projects require the Team or Enterprise plan.");
    }

    // --- kcap recap ---

    /// <summary>An empty recap is a success, and absence of entries is reported rather than rendered.</summary>
    [Test]
    public async Task Recap_reads_the_session_recap() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Json(200, "[]"));

        var run = await RunAsync("recap", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("No recap entries found.");

        var gets = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(gets.Count).IsEqualTo(1);
        await Assert.That(Header(gets[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
    }

    /// <summary>A 404 names the session that was not found and fails.</summary>
    [Test]
    public async Task Recap_reports_an_unknown_session() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var run = await RunAsync("recap", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains($"Session not found: {SessionId}");
    }

    // --- kcap validate-plan ---

    /// <summary>
    /// The artifacts route is always asked with <c>chain=true</c>, and an empty artifact set answers
    /// the question on its own — no recap call follows.
    /// </summary>
    [Test]
    public async Task Validate_plan_reads_the_plan_artifacts() {
        _server.Given(
                Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts")
                    .WithParam("chain", "true").UsingGet())
            .RespondWith(Json(200, """{"primary":null,"artifacts":[],"diagnostics":[]}"""));

        var run = await RunAsync("validate-plan", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("No plan found for this session.");

        var artifacts = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet());
        await Assert.That(artifacts.Count).IsEqualTo(1);
        await Assert.That(Header(artifacts[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");

        var recaps = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet());
        await Assert.That(recaps.Count).IsEqualTo(0);
    }

    /// <summary>A server without the artifacts route falls back to the recap route, still chain-widened.</summary>
    [Test]
    public async Task Validate_plan_falls_back_to_the_recap_route() {
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(
                Request.Create().WithPath($"/api/sessions/{SessionId}/recap")
                    .WithParam("chain", "true").UsingGet())
            .RespondWith(Json(200, "[]"));

        var run = await RunAsync("validate-plan", SessionId);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("No plan found for this session.");

        var recaps = _server.FindLogEntries(
            Request.Create().WithPath($"/api/sessions/{SessionId}/recap")
                .WithParam("chain", "true").UsingGet());
        await Assert.That(recaps.Count).IsEqualTo(1);
    }

    // --- kcap feedback ---

    /// <summary>A filed report is one POST carrying the category, the message and the CLI's own context.</summary>
    [Test]
    public async Task Feedback_posts_the_report() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Json(200, """{"reporter_email":"reporter@example.com"}"""));

        var run = await RunAsync("feedback", "--bug", "-m", "The boiler exploded");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.Stdout).Contains("Sent to Kurrent support as reporter@example.com");

        var posts = _server.FindLogEntries(Request.Create().WithPath("/api/feedback").UsingPost());
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonNode.Parse(posts[0].RequestMessage.Body!)!;
        await Assert.That(body["category"]?.GetValue<string>()).IsEqualTo("bug");
        await Assert.That(body["message"]?.GetValue<string>()).IsEqualTo("The boiler exploded");
        await Assert.That(body["context"]?["source"]?.GetValue<string>()).IsEqualTo("cli");
        await Assert.That(Header(posts[0], "Authorization")).IsEqualTo($"Bearer {BearerToken}");
        await Assert.That(Header(posts[0], "X-Kcap-Cli-Version")).IsNotNull();
    }

    /// <summary>Each refusal status has its own advice; a 409 means the account carries no email.</summary>
    [Test]
    public async Task Feedback_reports_a_reporter_without_an_email() {
        _server.Given(Request.Create().WithPath("/api/feedback").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409));

        var run = await RunAsync("feedback", "--feedback", "-m", "Nice tool");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Stderr).Contains("Your account has no email on file");
    }

    // --- fixtures ---

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
            .RespondWith(Json(200, """{"provider":"GitHubApp"}"""));

    static IResponseBuilder Json(int status, string body) =>
        Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);

    static string? Header(ILogEntry entry, string name) {
        if (entry.RequestMessage.Headers is not { } headers) return null;

        foreach (var header in headers) {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)) {
                return string.Join(',', header.Value);
            }
        }

        return null;
    }

    async Task<CliRun> RunAsync(params string[] args) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, args);
        psi.WorkingDirectory               = Tmp.Path;
        psi.Environment["KCAP_URL"]        = _server.Url!;
        psi.Environment["KCAP_SESSION_ID"] = "";
        psi.Environment["CODEX_THREAD_ID"] = "";
        psi.Environment["DO_NOT_TRACK"]    = "1";

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

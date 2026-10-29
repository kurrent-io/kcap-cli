using System.Diagnostics;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>Spawns the built CLI for <c>kcap sessions</c> against a stubbed server: the table and
/// the raw JSON render, an older server's 404 becomes one line, and a checkout without an origin
/// fails closed before any request is made.</summary>
public class SessionsCommandTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    const string Page =
        """{"items":[{"session_id":"s-1","slug":null,"title":"Fix the widget","owner":{"user_id":"github:1","username":"alice","display_name":"Alice","avatar_url":null},"vendor":"claude","status":"active","access_level":"full","stale":false,"started_at":"2026-09-02T09:00:00+00:00","ended_at":null,"last_activity_at":"2026-09-02T10:00:00+00:00","primary_repo_hash":"x","is_primary":true,"branch":"main","cwd":"/w","last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0}],"total":1,"limit":20,"offset":0}""";

    async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string workingDirectory, params string[] args) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, args);
        psi.WorkingDirectory = workingDirectory;
        psi.Environment["KCAP_URL"] = _server.Url!;

        using var proc   = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        var       stdout = await proc.StandardOutput.ReadToEndAsync();
        var       stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        return (proc.ExitCode, stdout, stderr);
    }

    [Test]
    public async Task Table_and_json_render_for_the_cwd_repo() {
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/widgets.git");
        var hash = RepoHashHelper.ComputeRepoHash("acme", "widgets");

        _server.Given(Request.Create().WithPath($"/api/repositories/{hash}/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Page));

        var table = await RunAsync(repo.Path, "sessions");
        await Assert.That(table.ExitCode).IsEqualTo(0);
        await Assert.That(table.StdOut).Contains("s-1");
        await Assert.That(table.StdOut).Contains("alice");
        await Assert.That(table.StdOut).Contains("Fix the widget");

        var json = await RunAsync(repo.Path, "sessions", "--json");
        await Assert.That(json.ExitCode).IsEqualTo(0);
        await Assert.That(json.StdOut.Trim()).IsEqualTo(Page);
    }

    [Test]
    public async Task Older_server_404_is_one_line_and_exit_1() {
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/widgets.git");

        _server.Given(Request.Create().WithPath("/api/repositories/*/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await RunAsync(repo.Path, "sessions");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.StdErr).Contains("Session listing needs a newer server; ask your admin to update.");
    }

    [Test]
    public async Task A_success_body_that_is_not_a_listing_is_reported_and_not_echoed() {
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/widgets.git");

        _server.Given(Request.Create().WithPath("/api/repositories/*/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("<html>Sign in</html>"));

        var json = await RunAsync(repo.Path, "sessions", "--json");

        await Assert.That(json.ExitCode).IsEqualTo(1);
        await Assert.That(json.StdErr).Contains("Unexpected response from the server.");
        await Assert.That(json.StdOut).DoesNotContain("<html>");
    }

    [Test]
    public async Task No_origin_and_no_repo_flag_fails_closed() {
        var result = await RunAsync(Tmp.Path, "sessions");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.StdErr).Contains("Not in a git repository with a remote origin.");
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path.Contains("/sessions"))).IsFalse();
    }

    const string PeriodPage =
        """{"items":[{"session_id":"s-9","slug":null,"title":"Ship the window","owner":{"user_id":"github:1","username":"alice","display_name":"Alice","avatar_url":null},"vendor":"claude","status":"ended","access_level":"full","stale":false,"started_at":"2026-09-20T09:00:00+00:00","ended_at":"2026-09-20T11:00:00+00:00","last_activity_at":"2026-09-20T11:00:00+00:00","primary_repo_hash":"x","is_primary":true,"branch":"main","cwd":"/w","last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0,"repo":{"hash":"x","owner":"acme","name":"widgets"}}],"total":2,"limit":20,"offset":0,"since":"2026-09-14T00:00:00+00:00","until":null,"next_cursor":"eyJ2IjoxfQ"}""";

    const string LastPage =
        """{"items":[{"session_id":"s-8","slug":null,"title":"Earlier work","owner":null,"vendor":"claude","status":"ended","access_level":"full","stale":false,"started_at":"2026-09-15T09:00:00+00:00","ended_at":"2026-09-15T11:00:00+00:00","last_activity_at":"2026-09-15T11:00:00+00:00","primary_repo_hash":null,"is_primary":false,"branch":null,"cwd":null,"last_prompt":null,"write_attempt_paths":[],"write_attempt_count":0,"repo":null}],"total":2,"limit":20,"offset":0,"since":"2026-09-14T00:00:00+00:00","until":null,"next_cursor":null}""";

    /// <summary>The working directory is no checkout at all, so reaching the server proves that
    /// listing every repository never looked for an origin remote.</summary>
    [Test]
    public async Task A_period_across_repositories_pages_outside_any_checkout() {
        _server.Given(Request.Create().WithPath("/api/sessions/listing").UsingGet()
                .WithParam("since", "2026-09-14T00:00:00Z").WithParam("state", "all"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(PeriodPage));
        _server.Given(Request.Create().WithPath("/api/sessions/listing").UsingGet().WithParam("cursor", "eyJ2IjoxfQ"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(LastPage));

        var first = await RunAsync(Tmp.Path, "sessions", "--repo", "all", "--since", "2026-09-14T00:00:00Z");

        await Assert.That(first.ExitCode).IsEqualTo(0);
        await Assert.That(first.StdOut).Contains("REPO");
        await Assert.That(first.StdOut).Contains("STARTED");
        await Assert.That(first.StdOut).Contains("acme/widgets");
        await Assert.That(first.StdOut).Contains("Ship the window");
        await Assert.That(first.StdOut).Contains("More: kcap sessions --repo all --cursor eyJ2IjoxfQ --limit 20");

        var second = await RunAsync(Tmp.Path, "sessions", "--repo", "all", "--cursor", "eyJ2IjoxfQ", "--limit", "20");

        await Assert.That(second.ExitCode).IsEqualTo(0);
        await Assert.That(second.StdOut).Contains("Earlier work");
        await Assert.That(second.StdOut).DoesNotContain("More:");
    }

    [Test]
    public async Task A_server_that_ignores_the_period_is_reported_and_lists_nothing() {
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/widgets.git");
        var hash = RepoHashHelper.ComputeRepoHash("acme", "widgets");

        _server.Given(Request.Create().WithPath($"/api/repositories/{hash}/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(Page));

        var table = await RunAsync(repo.Path, "sessions", "--since", "14d");

        await Assert.That(table.ExitCode).IsEqualTo(1);
        await Assert.That(table.StdErr).Contains("The time filter needs a newer server; ask your admin to update.");
        await Assert.That(table.StdOut).DoesNotContain("s-1");

        var json = await RunAsync(repo.Path, "sessions", "--since", "14d", "--json");

        await Assert.That(json.ExitCode).IsEqualTo(1);
        await Assert.That(json.StdOut).DoesNotContain("s-1");
    }

    [Test]
    public async Task Every_repository_on_an_older_server_is_one_line_and_exit_1() {
        _server.Given(Request.Create().WithPath("/api/sessions/listing").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await RunAsync(Tmp.Path, "sessions", "--repo", "all");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.StdErr).Contains("Session listing needs a newer server; ask your admin to update.");
    }
}

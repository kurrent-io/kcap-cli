using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>Spawns the built CLI for <c>kcap memories import</c> against a stubbed server: every note but the index is
/// saved, homed at the working directory's repository, and a dry run or an unconfirmable run sends nothing.</summary>
public class MemoriesImportTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempDir]         public required TempDir         Tmp     { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string workingDirectory, params string[] args) =>
        await RunAsync(workingDirectory, CancellationToken.None, args);

    async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string workingDirectory, CancellationToken ct, params string[] args) {
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"provider":"None"}"""));
        _server.Given(Request.Create().WithPath("/api/memories").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"memory":{"memory_id":"m1"},"near_duplicate":null,"slug_conflict":false}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, args);
        psi.WorkingDirectory = workingDirectory;
        psi.Environment["KCAP_URL"] = _server.Url!;

        using var proc   = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        var       stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var       stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        try {
            await proc.WaitForExitAsync(ct);
        } catch (OperationCanceledException) {
            proc.Kill();
            throw;
        }

        return (proc.ExitCode, await stdout, await stderr);
    }

    string MemoryFolder() {
        var dir = Tmp.CreateDir("memory");
        dir.CreateFile("MEMORY.md", "- [UTC](utc.md) — servers run UTC\n");
        dir.CreateFile("utc.md", "---\nname: utc-clock\ndescription: Servers run UTC\nmetadata:\n  type: feedback\n---\nConvert first.\n");
        dir.CreateFile("me.md", "---\nname: Terse Answers\ndescription: Answer briefly\ntype: user\n---\nKeep it short.\n");
        return dir.Path;
    }

    IReadOnlyList<JsonObject> Saves() =>
        [.. _server.FindLogEntries(Request.Create().WithPath("/api/memories").UsingPost())
            .Select(e => JsonNode.Parse(e.RequestMessage.Body!)!.AsObject())];

    [Test]
    public async Task Every_note_but_the_index_is_saved_at_the_working_directory_repository() {
        using var repo = GitRepo.Create();
        repo.AddRemote("https://github.com/acme/widgets.git");

        var result = await RunAsync(repo.Path, "memories", "import", "--from", MemoryFolder(), "--audience", "org", "--yes");

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StdErr);
        var saves = Saves().OrderBy(s => s["slug"]!.GetValue<string>(), StringComparer.Ordinal).ToList();
        await Assert.That(saves.Select(s => s["slug"]!.GetValue<string>())).IsEquivalentTo(["terse-answers", "utc-clock"]);
        await Assert.That(saves[0]["kind"]!.GetValue<string>()).IsEqualTo("preference");
        await Assert.That(saves[1]["kind"]!.GetValue<string>()).IsEqualTo("feedback");
        await Assert.That(saves.All(s => s["audience"]!.GetValue<string>() == "org")).IsTrue();
        await Assert.That(saves.All(s => s["repo_hash"]!.GetValue<string>() == RepoHashHelper.ComputeRepoHash("acme", "widgets"))).IsTrue();
        await Assert.That(saves.All(s => s["harness"]!.GetValue<string>() == "kcap-import")).IsTrue();
    }

    [Test]
    public async Task A_dry_run_shows_the_plan_and_sends_nothing() {
        var result = await RunAsync(Tmp.Path, "memories", "import", "--from", MemoryFolder(), "--global", "--dry-run");

        await Assert.That(result.ExitCode).IsEqualTo(0).Because(result.StdErr);
        await Assert.That(result.StdOut).Contains("utc-clock");
        await Assert.That(result.StdOut).Contains("the whole org");
        await Assert.That(Saves()).IsEmpty();
    }

    /// <summary>Pins the refusal with stdin left open and never written, which is what made the prompt wait forever.</summary>
    [Test]
    public async Task Without_a_terminal_and_without_yes_it_refuses_promptly_and_sends_nothing() {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var result = await RunAsync(Tmp.Path, timeout.Token, "memories", "import", "--from", MemoryFolder(), "--global");

        await Assert.That(result.ExitCode).IsEqualTo(1);
        await Assert.That(result.StdOut).Contains("utc-clock");
        await Assert.That(result.StdOut).DoesNotContain("Import? [y/N]");
        await Assert.That(result.StdErr).Contains("Pass --yes");
        await Assert.That(Saves()).IsEmpty();
    }
}

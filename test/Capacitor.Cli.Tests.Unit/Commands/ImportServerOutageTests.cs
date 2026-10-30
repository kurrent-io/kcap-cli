using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// An import whose probes fail: the run must fail, say why when the server is down, and keep an
/// ignored session excluded rather than counting it as a failure to retry.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportServerOutageTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A projects tree holding one session in an ignored directory and one outside it.</summary>
    (string ProjectsDir, string IgnoredDir) WriteSessions() {
        var ignoredDir  = Tmp.CreateDir("ignored");
        var keptDir     = Tmp.CreateDir("kept");
        var projectsDir = Tmp.CreateDir("projects");

        Write("-ignored", "ignored-sess", ignoredDir);
        Write("-kept", "kept-sess", keptDir);

        return (projectsDir.Path, ignoredDir.Path);

        void Write(string encoded, string sessionId, string cwd) {
            var cwdJson = JsonSerializer.Serialize(cwd);
            projectsDir.CreateDir(encoded).CreateFile($"{sessionId}.jsonl", Enumerable.Range(0, 20).Select(i =>
                $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":{{{cwdJson}}},"message":{"content":"line-{{{i}}}"}}""").ToArray());
        }
    }

    async Task<ImportCommand.ImportRunOutcome> Import(string serverUrl, TimeProvider time, TestImportTerminal terminal) {
        var (projectsDir, ignoredDir) = WriteSessions();
        ImportCommand.ImportRunOutcome? outcome = null;

        var exit = await new ImportCommand(Config.Root,
                Resolutions.Of(new Profile { ExcludedPaths = [ignoredDir] }, "outage-test", serverUrl), Home,
                TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new GitProviderRouter(), time, console: terminal.Console)
            .HandleImport(
                filterCwd: null,
                minLines: 1,
                sources: [new ClaudeImportSource(Config.Root, projectsDir, new GitProviderRouter(), time)],
                scope: new ImportScope.All(),
                skipConfirmation: true,
                skipTitle: true,
                onFinished: o => outcome = o);

        await Assert.That(exit).IsEqualTo(0);

        return outcome!;
    }

    [Test]
    public async Task An_unreachable_server_is_named_and_fails_the_run_without_counting_the_ignored_session() {
        var terminal = new TestImportTerminal();
        var url      = $"http://127.0.0.1:{ClosedPort()}";

        // Each probe's retry budget runs out on its first refusal instead of after thirty real seconds.
        var outcome = await Import(url, new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromMinutes(3) }, terminal);

        await Assert.That(outcome.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.ProbeError).IsEqualTo(1);
        await Assert.That(outcome.ExitCode).IsEqualTo(1);
        await Assert.That(terminal.Error.ToString()).Contains($"Could not reach the kcap server at {url}");
    }

    [Test]
    public async Task A_server_that_answers_with_errors_fails_the_run_without_calling_itself_unreachable() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500));

        var terminal = new TestImportTerminal();
        var outcome  = await Import(server.Url!, TimeProvider.System, terminal);

        await Assert.That(outcome.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.ProbeError).IsEqualTo(1);
        await Assert.That(outcome.ExitCode).IsEqualTo(1);
        await Assert.That(terminal.Error.ToString()).DoesNotContain("Could not reach");
        await Assert.That(server.LogEntries.Where(e => e.RequestMessage.Path.StartsWith("/hooks/", StringComparison.Ordinal)))
            .IsEmpty();
    }

    static int ClosedPort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}

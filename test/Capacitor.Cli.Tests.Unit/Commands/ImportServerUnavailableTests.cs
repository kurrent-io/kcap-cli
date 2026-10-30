using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A run against a server that answers nothing fails and says why, and its capture exclusions hold
/// exactly as they do online.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportServerUnavailableTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    static void WriteSession(TempDirHandle dir, string sessionId, string cwd) {
        var cwdJson = JsonSerializer.Serialize(cwd);

        dir.CreateFile($"{sessionId}.jsonl", [
            .. Enumerable.Range(0, 20).Select(i =>
                "{\"type\":\"user\",\"timestamp\":\"2026-03-15T10:00:00Z\",\"cwd\":" + cwdJson
              + ",\"message\":{\"content\":\"line-" + i + "\"}}")
        ]);
    }

    static ImportCommand.SessionClassification Classified(ImportCommand.ClassificationStatus status, string? reason = null) => new() {
        SessionId        = "s1",
        FilePath         = "",
        EncodedCwd       = "",
        Meta             = new SessionMetadata(),
        Status           = status,
        ProbeErrorReason = reason,
    };

    static ImportCommand.FinalCounts Counts(int loaded = 0, int alreadyLoaded = 0, int probeError = 0, int errored = 0, int excluded = 0) =>
        new(loaded, 0, alreadyLoaded, 0, excluded, probeError, errored, 0, 0, 0, 0, 0, RanBackground: false, RequestedSummaries: false);

    [Test]
    public async Task An_unavailable_server_fails_the_run_names_the_server_and_keeps_the_exclusion() {
        var excluded = Tmp.CreateDir("secret-project");
        var open     = Tmp.CreateDir("open-project");
        var projects = Tmp.CreateDir("projects");
        WriteSession(projects.CreateDir("-secret-project"), "secret-sess", excluded);
        WriteSession(projects.CreateDir("-open-project"), "open-sess", open);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingAnyMethod()).RespondWith(Response.Create().WithStatusCode(503));

        var terminal = new TestImportTerminal();
        ImportCommand.ImportRunOutcome? outcome = null;

        var import = new ImportCommand(Config.Root,
            Resolutions.Of(new Profile { ExcludedPaths = [excluded.Path] }, "offline-test", server.Url!), Home,
            TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new GitProviderRouter(), TimeProvider.System, console: terminal.Console);

        var handled = await import.HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects.Path, new GitProviderRouter(), TimeProvider.System)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            skipTitle: true,
            onFinished: o => outcome = o);

        // The same split an online run makes: the excluded session is Excluded, not a probe error.
        await Assert.That(outcome!.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.ProbeError).IsEqualTo(1);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(0);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path.Contains("secret-sess", StringComparison.Ordinal))).IsFalse();

        await Assert.That(ImportExitCode.For(handled, outcome)).IsEqualTo(1);
        await Assert.That(terminal.Error.ToString()).Contains("looks unavailable");
        await Assert.That(terminal.Error.ToString()).Contains("HTTP 503");
    }

    [Test]
    public async Task No_answer_means_probes_failed_and_nothing_reached_the_server() {
        await Assert.That(Counts(probeError: 3, excluded: 2).NoAnswerFromServer).IsTrue();
        await Assert.That(Counts(probeError: 3, loaded: 1).NoAnswerFromServer).IsFalse();
        await Assert.That(Counts(probeError: 3, alreadyLoaded: 1).NoAnswerFromServer).IsFalse();
        await Assert.That(Counts(probeError: 3, errored: 1).NoAnswerFromServer).IsFalse();
        await Assert.That(Counts(excluded: 2).NoAnswerFromServer).IsFalse();
    }

    [Test]
    public async Task The_notice_names_the_server_without_its_credentials_and_the_first_reason() {
        var notice = ImportCommand.ServerUnavailableNotice(
            "https://user:hunter2@kcap.example",
            [Classified(ImportCommand.ClassificationStatus.ProbeError, "HTTP 503\nforged line"),
             Classified(ImportCommand.ClassificationStatus.ProbeError),
             Classified(ImportCommand.ClassificationStatus.Excluded)]);

        await Assert.That(notice).Contains("kcap.example");
        await Assert.That(notice).DoesNotContain("hunter2");
        await Assert.That(notice).Contains("answered none of 2 probe(s) (HTTP 503forged line)");
        await Assert.That(notice).DoesNotContain("\n");
    }
}

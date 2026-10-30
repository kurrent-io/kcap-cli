using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using Spectre.Console;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// An import that cannot ask — <c>--yes</c> at a terminal, or no terminal at all — keeps every capture
/// exclusion, never reads stdin for one, and never probes the server about a session it excludes.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class ImportExclusionTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    const string Secret = "secret-sess";
    const string Open   = "open-sess";

    /// <summary>One session under the excluded directory, one beside it.</summary>
    (string Projects, string Excluded) Fixture() {
        var excluded = Tmp.CreateDir("secret-project");
        var open     = Tmp.CreateDir("open-project");
        var projects = Tmp.CreateDir("projects");

        WriteSession(projects.CreateDir("-secret-project"), Secret, excluded);
        WriteSession(projects.CreateDir("-open-project"), Open, open);

        return (projects.Path, excluded.Path);
    }

    static void WriteSession(TempDirHandle dir, string sessionId, string cwd) {
        // Serialized so a Windows path's backslashes are escaped and the transcript still names its cwd.
        var cwdJson = JsonSerializer.Serialize(cwd);

        dir.CreateFile($"{sessionId}.jsonl", [
            .. Enumerable.Range(0, 20).Select(i =>
                "{\"type\":\"user\",\"timestamp\":\"2026-03-15T10:00:00Z\",\"cwd\":" + cwdJson
              + ",\"message\":{\"content\":\"line-" + i + "\"}}")
        ]);
    }

    static WireMockServer Online() {
        var server = WireMockServer.Start();

        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/set-title", "/hooks/session-title" })
            server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

        return server;
    }

    static bool Asked(WireMockServer server, string sessionId) =>
        server.LogEntries.Any(e => e.RequestMessage.Path.Contains(sessionId, StringComparison.Ordinal));

    async Task<(int Exit, ImportCommand.ImportRunOutcome? Outcome)> RunAsync(
            WireMockServer server, string projects, string excluded, ImportConsole console) {
        ImportCommand.ImportRunOutcome? outcome = null;

        var import = new ImportCommand(Config.Root,
            Resolutions.Of(new Core.Config.Profile { ExcludedPaths = [excluded] }, "exclusion-test", server.Url!), Home,
            TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new GitProviderRouter(), TimeProvider.System, console: console);

        var exit = await import.HandleImport(
            filterCwd: null,
            minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects, new GitProviderRouter(), TimeProvider.System)],
            scope: new ImportScope.All(),
            skipConfirmation: true,
            skipTitle: true,
            onFinished: o => outcome = o);

        return (exit, outcome);
    }

    [Test]
    public async Task Yes_at_a_terminal_keeps_the_exclusion_without_reading_stdin() {
        // The terminal case: stdin and stdout are both a TTY, so only --yes stands between the run and
        // a prompt. A "y" waiting on stdin would include the session if anything read it.
        var (projects, excluded) = Fixture();
        using var server = Online();

        var stdin = new StringReader("y\ny\ny\n");
        var error = new StringWriter { NewLine = "\n" };
        var ansi  = AnsiConsole.Create(new AnsiConsoleSettings {
            Ansi        = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out         = new AnsiConsoleOutput(new StringWriter()),
        });
        ansi.Profile.Width = 120;

        var console = new ImportConsole(new StringWriter(), error, stdin, ansi, OutputRedirected: false, InputRedirected: false);

        var (exit, outcome) = await RunAsync(server, projects, excluded, console);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdin.Peek()).IsEqualTo((int)'y');
        await Assert.That(error.ToString()).DoesNotContain("is excluded. Include");
        await Assert.That(error.ToString()).Contains("Auto-skipping 1 session(s) from 1 excluded source(s) (--yes keeps exclusions).");
        await Assert.That(outcome!.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(1);
        await Assert.That(Asked(server, Secret)).IsFalse();
        await Assert.That(Asked(server, Open)).IsTrue();
    }

    [Test]
    public async Task Redirected_stdin_keeps_the_exclusion_and_never_probes_it() {
        var (projects, excluded) = Fixture();
        using var server = Online();
        var terminal = new TestImportTerminal();

        var (exit, outcome) = await RunAsync(server, projects, excluded, terminal.Console);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(terminal.Error.ToString()).Contains("Auto-skipping 1 session(s)");
        await Assert.That(outcome!.Counts.Excluded).IsEqualTo(1);
        await Assert.That(outcome.Counts.Imported).IsEqualTo(1);
        await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
        await Assert.That(Asked(server, Secret)).IsFalse();
    }

    [Test]
    [Arguments(true, false, false, false, true)]
    [Arguments(true, false, false, true, false)]
    [Arguments(true, true, false, false, false)]
    [Arguments(false, false, false, false, false)]
    [Arguments(true, false, true, false, false)]
    public async Task Only_a_terminal_run_without_yes_may_ask_to_include_an_exclusion(
            bool tty, bool inputRedirected, bool autoSkipExclusions, bool skipConfirmation, bool expected) =>
        await Assert.That(ImportCommand.CanPromptForExclusions(tty, inputRedirected, autoSkipExclusions, skipConfirmation))
            .IsEqualTo(expected);

    [Test]
    public async Task The_yes_notice_names_how_to_include_an_exclusion_and_setup_s_does_not() {
        await Assert.That(ImportCommand.AutoSkipNotice(2, 1, skipConfirmation: true, autoSkipExclusions: false))
            .IsEqualTo("Auto-skipping 2 session(s) from 1 excluded source(s) (--yes keeps exclusions). "
                     + "To include them, run without --yes at a terminal and answer its prompt.");
        await Assert.That(ImportCommand.AutoSkipNotice(2, 1, skipConfirmation: true, autoSkipExclusions: true))
            .IsEqualTo("Auto-skipping 2 session(s) from 1 excluded source(s) (non-interactive).");
    }
}

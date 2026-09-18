using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// <summary>Drives the runner's real process seam with a fake `claude` script on PATH — the same
/// technique <c>ImportSkipTitleTests</c> uses — to pin each <see cref="ClaudeCliFailure"/> kind and
/// the success-behind-a-non-zero-exit recovery. PATH mutation is process-global, so the whole class
/// runs exclusive of the rest of the assembly.</summary>
[NotInParallel]
public class ClaudeCliRunnerDetailedTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task RunDetailedAsync_returns_timeout_when_the_process_outlives_the_deadline() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");

        using var fake = new FakeClaudeOnPath("#!/bin/sh\nsleep 5\n");

        var outcome = await ClaudeCliRunner.RunDetailedAsync(
            "irrelevant", TimeSpan.FromMilliseconds(300), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.FromEnvironment()));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);

        var legacy = await ClaudeCliRunner.RunAsync(
            "irrelevant", TimeSpan.FromMilliseconds(300), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.FromEnvironment()));
        await Assert.That(legacy).IsNull();
    }

    [Test]
    public async Task RunDetailedAsync_returns_timeout_when_the_deadline_elapses_mid_stdin_write() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");

        // Never reads stdin, so a prompt well past any OS pipe buffer size blocks WriteAsync until
        // the internal deadline cancels it — the stdin-write path, not the process-exit path.
        using var fake = new FakeClaudeOnPath("#!/bin/sh\nsleep 5\n");
        var bigPrompt = new string('x', 4 * 1024 * 1024);

        var outcome = await ClaudeCliRunner.RunDetailedAsync(
            bigPrompt, TimeSpan.FromMilliseconds(300), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.FromEnvironment()), promptViaStdin: true);

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);
    }

    [Test]
    public async Task RunDetailedAsync_returns_process_failure_on_non_zero_exit_with_no_recoverable_result() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");

        using var fake = new FakeClaudeOnPath("#!/bin/sh\necho 'boom' 1>&2\nexit 1\n");

        var outcome = await ClaudeCliRunner.RunDetailedAsync(
            "irrelevant", TimeSpan.FromSeconds(10), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.FromEnvironment()));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
    }

    [Test]
    public async Task RunDetailedAsync_keeps_success_when_a_parseable_result_is_recovered_from_a_non_zero_exit() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");

        using var fake = new FakeClaudeOnPath("""
            #!/bin/sh
            echo '{"result":"ok"}'
            exit 1
            """);

        var outcome = await ClaudeCliRunner.RunDetailedAsync(
            "irrelevant", TimeSpan.FromSeconds(10), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.FromEnvironment()));

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result).IsNotNull();
        await Assert.That(outcome.Result!.Result).IsEqualTo("ok");

        var legacy = await ClaudeCliRunner.RunAsync(
            "irrelevant", TimeSpan.FromSeconds(10), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.FromEnvironment()));
        await Assert.That(legacy).IsNotNull();
        await Assert.That(legacy!.Result).IsEqualTo("ok");
    }

    [Test]
    public async Task RunDetailedAsync_returns_output_unparseable_on_zero_exit_with_no_parseable_result() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");

        // Starts with '{' so ParseResponse's plain-text fallback does not kick in, and carries no
        // session_id so the transcript fallback also fails — genuinely unparseable output.
        using var fake = new FakeClaudeOnPath("#!/bin/sh\necho '{not valid json'\n");

        var outcome = await ClaudeCliRunner.RunDetailedAsync(
            "irrelevant", TimeSpan.FromSeconds(10), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.FromEnvironment()));

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.OutputUnparseable);
    }

    /// <summary>Puts a `claude` on PATH running the given shell script. Mirrors
    /// <c>ImportSkipTitleTests.FakeClaudeOnPath</c>.</summary>
    sealed class FakeClaudeOnPath : IDisposable {
        readonly TempDir  _bin;
        readonly EnvScope _path;

        public FakeClaudeOnPath(string script) {
            _bin = new TempDir();

            var path = _bin.CreateFile("claude", script);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            _path = EnvScope.Exclusive(
                "PATH", _bin.Path + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }

        public void Dispose() {
            _path.Dispose();
            _bin.Dispose();
        }
    }
}

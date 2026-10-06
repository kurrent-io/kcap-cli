using System.Globalization;

namespace Capacitor.Cli.Core.Tests.Unit;

/// Drives the real ProcessRunner's bounded capture against real children. Without the bound the flooding child
/// sleeps until the deadline, so an unbounded capture comes back TimedOut rather than Oversized.
public class ProcessRunnerBoundedTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly IProcessRunner Runner = new ProcessRunner(TimeProvider.System);
    const int Limit = 64 * 1024;
    static readonly RunOptions Options = new(Timeout: TimeSpan.FromSeconds(5));

    // Records its pid and waits for the go file, so the test captures the child's identity before it can die.
    string Flood() => Tmp.CreateExecutable("flood.sh", """
        #!/bin/sh
        echo $$ > "$1"
        while [ ! -e "$2" ]; do sleep 0.02; done
        head -c "$3" /dev/zero | tr '\0' x >&"$4"
        exec sleep 30
        """);

    async Task<(ProcessResult Result, int Pid, string Identity)> RunFloodAsync(int bytes, int fd) {
        var pidFile = Tmp.PathTo("pid");
        var go = Tmp.PathTo("go");
        var run = Runner.RunBoundedAsync(
            Flood(), [pidFile, go, bytes.ToString(CultureInfo.InvariantCulture), fd.ToString(CultureInfo.InvariantCulture)],
            Options, Limit, CancellationToken.None);
        var pid = await ReadPidAsync(pidFile);
        var identity = PidIdentity.Capture(pid);
        await File.WriteAllTextAsync(go, "");
        return (await run, pid, identity);
    }

    static async Task<int> ReadPidAsync(string path) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true) {
            if (File.Exists(path) && int.TryParse((await File.ReadAllTextAsync(path)).Trim(), CultureInfo.InvariantCulture, out var pid) && pid > 0)
                return pid;
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the child never recorded its pid");
            await Task.Delay(10);
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task A_stream_over_the_limit_ends_the_run_as_oversized_and_kills_the_child(int fd) {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX script");

        var (result, pid, identity) = await RunFloodAsync(Limit * 4, fd);

        await Assert.That(result.Oversized).IsTrue();
        await Assert.That(result.TimedOut).IsFalse();
        await Assert.That(result.Stdout).IsEmpty();
        await Assert.That(result.Stderr).IsEmpty();
        await Assert.That(PidIdentity.IsGone(pid, identity)).IsTrue();
    }

    [Test]
    public async Task Output_at_the_limit_on_both_streams_is_returned_whole() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX script");

        var script = Tmp.CreateExecutable("both.sh", $"""
            #!/bin/sh
            head -c {Limit} /dev/zero | tr '\0' o
            head -c {Limit} /dev/zero | tr '\0' e >&2
            """);

        var result = await Runner.RunBoundedAsync(script, [], Options, Limit, CancellationToken.None);

        await Assert.That(result.Oversized).IsFalse();
        await Assert.That(result.ExitCode).IsEqualTo(0);
        await Assert.That(result.Stdout).IsEqualTo(new string('o', Limit));
        await Assert.That(result.Stderr).IsEqualTo(new string('e', Limit));
    }
}

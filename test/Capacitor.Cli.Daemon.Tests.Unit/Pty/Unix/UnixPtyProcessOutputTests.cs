using System.Text;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>What a consumer of <see cref="UnixPtyProcess.ReadOutputAsync"/> relies on, driven
/// through real PTYs: order, back-pressure, quiet cancellation and disposal.</summary>
[ParallelLimiter<SubprocessLimit>]
public class UnixPtyProcessOutputTests {
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Test]
    public async Task Output_arrives_complete_and_in_order_and_ends_when_the_child_exits() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        const int lines = 20_000; // well past one chunk, so ordering across chunks is exercised

        using var spawner = new UnixSpawnerThread();
        var       proc    = Spawn(spawner, "/bin/sh", "-c", $"seq 1 {lines}");
        try {
            var output = await ReadToEndAsync(proc).WaitAsync(Bound);
            await proc.WaitForExitAsync(TimeSpan.FromSeconds(5));

            // Carriage returns are the kernel's, not the child's: the line discipline rewrites "\n"
            // as "\r\n", and macOS emits "\r\r\n" when its output queue fills mid-rewrite.
            var actual   = Encoding.ASCII.GetString(output).Replace("\r", "", StringComparison.Ordinal);
            var expected = string.Concat(Enumerable.Range(1, lines).Select(i => $"{i}\n"));

            await Assert.That(actual == expected).IsTrue();
            await Assert.That(proc.ExitCode).IsEqualTo(0);
        } finally {
            await proc.DisposeAsync();
        }
    }

    /// <summary>The child can only still be running because nobody drained its output: read ahead
    /// without bound and it finishes inside the wait. A slow machine makes that wait prove less,
    /// never fail.</summary>
    [Test]
    public async Task An_unread_pty_back_pressures_the_child_and_loses_nothing() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        const int bytes = 2_000_000;

        using var spawner = new UnixSpawnerThread();
        var       proc    = Spawn(spawner, "/bin/sh", "-c", $"head -c {bytes} /dev/zero | tr '\\000' x");
        try {
            await proc.WaitForExitAsync(TimeSpan.FromMilliseconds(500));
            await Assert.That(proc.HasExited).IsFalse();

            var output = await ReadToEndAsync(proc).WaitAsync(Bound);

            await Assert.That(output.Length).IsEqualTo(bytes);
            await Assert.That(output.All(b => b == (byte)'x')).IsTrue();
        } finally {
            await proc.DisposeAsync();
        }
    }

    [Test]
    public async Task Cancelling_a_read_ends_it_quietly_and_a_later_read_picks_up_where_it_left() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       proc    = Spawn(spawner, "/bin/cat"); // cooked mode: the kernel echoes input
        try {
            using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100))) {
                await ReadToEndAsync(proc, cancelled.Token).WaitAsync(Bound);
            }

            await proc.WriteAsync("x");

            using var found = new CancellationTokenSource(Bound);
            var       echo  = new List<byte>();

            await foreach (var chunk in proc.ReadOutputAsync(found.Token)) {
                echo.AddRange(chunk);

                if (echo.Contains((byte)'x')) break;
            }

            await Assert.That(echo).Contains((byte)'x');
        } finally {
            await proc.DisposeAsync();
        }
    }

    [Test]
    public async Task Dispose_ends_a_parked_read_quietly() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       proc    = Spawn(spawner, "/bin/cat");

        var read = ReadToEndAsync(proc);
        await Task.Delay(100);

        await proc.DisposeAsync().AsTask().WaitAsync(Bound);
        await read.WaitAsync(Bound);
    }

    static IPtyProcess Spawn(UnixSpawnerThread spawner, string command, params string[] args)
        => new UnixPtyProcessFactory(spawner, TimeProvider.System).Spawn(command, args, AppContext.BaseDirectory);

    static async Task<byte[]> ReadToEndAsync(IPtyProcess proc, CancellationToken ct = default) {
        var output = new List<byte>();

        await foreach (var chunk in proc.ReadOutputAsync(ct)) output.AddRange(chunk);

        return [.. output];
    }
}

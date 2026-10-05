using Capacitor.Cli.Daemon.Pty.Unix;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>Input that reaches a <see cref="UnixPtyProcess"/> while or after it is disposed must
/// never touch the master fd once it closed: the kernel hands the lowest free number to the next
/// open, so a late write lands in whatever file took it.</summary>
[ParallelLimiter<SubprocessLimit>]
public class UnixPtyProcessDisposalTests {
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Test]
    public async Task A_write_after_dispose_does_nothing_and_never_reaches_the_reused_fd() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       proc    = Spawn(spawner, "/bin/cat");
        var       fd      = proc.MasterFdForTest;

        await proc.DisposeAsync().AsTask().WaitAsync(Bound);

        using var reuse = new FdReuse(fd);

        await proc.WriteAsync("late").WaitAsync(Bound);
        proc.Resize(80, 24);

        await Assert.That(reuse.WrittenBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task Writes_racing_dispose_never_reach_the_reused_fd() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var spawner = new UnixSpawnerThread();
        var       proc    = Spawn(spawner, "/bin/cat");
        var       fd      = proc.MasterFdForTest;

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () => {
                // Bounded so the unread echo can never fill the PTY and park a write.
                for (var i = 0; i < 400 && !stop.IsCancellationRequested; i++) {
                    await proc.WriteAsync("x");
                    await Task.Delay(1);
                }
            }
        );

        await Task.Delay(20);
        await proc.DisposeAsync().AsTask().WaitAsync(Bound);

        using var reuse = new FdReuse(fd);
        await Task.Delay(50);
        await stop.CancelAsync();
        await writer.WaitAsync(Bound);

        await Assert.That(reuse.WrittenBytes).IsEqualTo(0L);
    }

    static UnixPtyProcess Spawn(UnixSpawnerThread spawner, string command, params string[] args)
        => UnixPtyProcess.Spawn(spawner, command, args, AppContext.BaseDirectory, TimeProvider.System);

    /// <summary>Opens files until one is given <c>fd</c>. If another thread takes the number first
    /// the test proves less, never fails: <see cref="WrittenBytes"/> stays 0 either way.</summary>
    sealed class FdReuse : IDisposable {
        readonly string               _dir     = Directory.CreateTempSubdirectory("kcap-fd-reuse-").FullName;
        readonly List<SafeFileHandle> _handles = [];
        readonly string?              _path;

        public FdReuse(int fd) {
            for (var i = 0; i < 32 && _path is null; i++) {
                var path   = Path.Combine(_dir, $"{i}.bin");
                var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write);
                _handles.Add(handle);

                if ((int)handle.DangerousGetHandle() == fd) _path = path;
            }
        }

        public long WrittenBytes => _path is null ? 0 : new FileInfo(_path).Length;

        public void Dispose() {
            foreach (var handle in _handles) handle.Dispose();

            Directory.Delete(_dir, recursive: true);
        }
    }
}

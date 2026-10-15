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

    /// <summary>When the process cannot be built around a spawned master, the fd is closed once: a
    /// second close from the orphaned handle's finalizer would hit whatever file reused the number.</summary>
    [Test]
    public async Task A_failed_adoption_leaves_the_reused_fd_open() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        using var tmp   = new TempDir();
        var       stand = File.OpenHandle(tmp.CreateFile("master.bin"), FileMode.Open, FileAccess.Write);
        var       fd    = (int)stand.DangerousGetHandle();
        stand.SetHandleAsInvalid(); // the fd is Adopt's to close now

        await Assert.That(() => UnixPtyProcess.Adopt<object>(fd, pid: 0, _ => throw new InvalidOperationException()))
            .Throws<InvalidOperationException>();

        using var reuse = new FdReuse(fd);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (reuse.Taker is null) return; // another thread took the number: nothing to prove

        var written = UnixPtyInterop.write(fd, "ok"u8.ToArray(), 2);
        if (written < 0) reuse.Taker.SetHandleAsInvalid(); // closed under it: disposing it would close a third file

        await Assert.That(written).IsEqualTo(2);
        await Assert.That(reuse.WrittenBytes).IsEqualTo(2L);
    }

    static UnixPtyProcess Spawn(UnixSpawnerThread spawner, string command, params string[] args)
        => UnixPtyProcess.Spawn(spawner, command, args, AppContext.BaseDirectory, TimeProvider.System);

    /// <summary>Opens files until one is given <c>fd</c>. If another thread takes the number first
    /// the test proves less, never fails: <see cref="WrittenBytes"/> stays 0 either way.</summary>
    sealed class FdReuse : IDisposable {
        readonly TempDir              _tmp     = new("fd-reuse");
        readonly List<SafeFileHandle> _handles = [];
        readonly string?              _path;

        public FdReuse(int fd) {
            for (var i = 0; i < 32 && _path is null; i++) {
                var path   = _tmp.PathTo($"{i}.bin");
                var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write);
                _handles.Add(handle);

                if ((int)handle.DangerousGetHandle() != fd) continue;

                _path = path;
                Taker = handle;
            }
        }

        /// <summary>The handle that was given <c>fd</c>, if one was.</summary>
        public SafeFileHandle? Taker { get; }

        public long WrittenBytes => _path is null ? 0 : new FileInfo(_path).Length;

        public void Dispose() {
            foreach (var handle in _handles) handle.Dispose();

            _tmp.Dispose();
        }
    }
}

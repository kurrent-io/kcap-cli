using System.Net.Sockets;
using TUnit.Core.Enums;
using Capacitor.App.Platform.Linux;

namespace Capacitor.App.Tests.Unit;

public class LinuxSingleInstanceLockTests {
    [TempDir("short")] public required TempDir Tmp { get; init; }

    LinuxSingleInstanceLock? Acquire(TimeSpan wait) =>
        LinuxSingleInstanceLock.Acquire(Tmp.Path, wait, TimeProvider.System, pid => throw new InvalidOperationException($"ended {pid}"));

    [Test]
    public async Task A_second_launch_is_refused_and_brings_the_running_copy_forward() {
        using var first = Acquire(TimeSpan.Zero)!;
        var shown = new TaskCompletionSource();
        first.ShowRequested += () => shown.TrySetResult();

        await Assert.That(Acquire(TimeSpan.Zero)).IsNull();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// A holder that never listens stands in for a suspended copy; ending it releases its lock.
    [Test, RunOn(OS.Linux)]
    public async Task A_holder_that_never_answers_is_ended_and_its_lock_taken() {
        var held = new FileStream(Tmp.PathTo("kcap-app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var silent = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        silent.Bind(new UnixDomainSocketEndPoint(Tmp.PathTo("kcap-app.sock")));
        silent.Listen();
        int? ended = null;

        using var second = LinuxSingleInstanceLock.Acquire(Tmp.Path, TimeSpan.Zero, TimeProvider.System, pid => {
            ended = pid;
            held.Dispose();
        });

        await Assert.That(ended).IsEqualTo(Environment.ProcessId);
        await Assert.That(second).IsNotNull();
    }

    [Test]
    public async Task A_relaunched_copy_waits_for_the_old_one_to_release_its_lock() {
        var first = Acquire(TimeSpan.Zero)!;
        var waiting = Task.Run(() => Acquire(TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        await Assert.That(waiting.IsCompleted).IsFalse();

        first.Dispose();
        using var second = await waiting;

        await Assert.That(second).IsNotNull();
    }

    [Test]
    public async Task A_socket_left_by_a_crashed_copy_does_not_block_the_next_launch() {
        Tmp.CreateFile("kcap-app.sock", "");

        using var instance = Acquire(TimeSpan.Zero);

        await Assert.That(instance).IsNotNull();
    }

    [Test]
    public async Task A_directory_too_deep_for_a_unix_socket_cannot_host_the_lock() {
        await Assert.That(LinuxSingleInstanceLock.FitsIn("/run/user/1000")).IsTrue();
        await Assert.That(LinuxSingleInstanceLock.FitsIn("/" + new string('d', 100))).IsFalse();
    }
}

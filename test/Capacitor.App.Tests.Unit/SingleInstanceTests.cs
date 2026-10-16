using Capacitor.App.Platform.Linux;

namespace Capacitor.App.Tests.Unit;

public class SingleInstanceTests {
    [TempDir("short")] public required TempDir Tmp { get; init; }

    SingleInstance? Claim(TimeSpan wait) => SingleInstance.Claim(Tmp.Path, wait, TimeProvider.System);

    [Test]
    public async Task A_second_launch_is_refused_and_brings_the_running_copy_forward() {
        using var first = Claim(TimeSpan.Zero)!;
        var shown = new TaskCompletionSource();
        first.Listen(() => shown.TrySetResult());

        await Assert.That(Claim(TimeSpan.Zero)).IsNull();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_relaunched_copy_waits_for_the_old_one_to_release_its_claim() {
        var first = Claim(TimeSpan.Zero)!;
        var waiting = Task.Run(() => Claim(TimeSpan.FromSeconds(10)));
        await Task.Delay(300);
        await Assert.That(waiting.IsCompleted).IsFalse();

        first.Dispose();
        using var second = await waiting;

        await Assert.That(second).IsNotNull();
    }

    [Test]
    public async Task A_socket_left_by_a_crashed_copy_does_not_block_the_next_launch() {
        Tmp.CreateFile("kcap-app.sock", "");

        using var instance = Claim(TimeSpan.Zero);

        await Assert.That(instance).IsNotNull();
    }

    [Test]
    public async Task A_directory_too_deep_for_a_unix_socket_cannot_host_the_claim() {
        await Assert.That(SingleInstance.FitsIn("/run/user/1000")).IsTrue();
        await Assert.That(SingleInstance.FitsIn("/" + new string('d', 100))).IsFalse();
    }
}

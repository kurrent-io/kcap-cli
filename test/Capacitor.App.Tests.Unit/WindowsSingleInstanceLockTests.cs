using System.Runtime.Versioning;
using Capacitor.App.Platform.Windows;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

/// A Win32 mutex is re-entrant on its owning thread, so every acquirer here runs on a dedicated
/// thread — on a shared one the second TryAcquire would succeed and prove nothing.
[RunOn(OS.Windows)]
[SupportedOSPlatform("windows")]
public class WindowsSingleInstanceLockTests {
    static string UniqueName() => $"kcap-test-{Guid.NewGuid():N}";

    static Task<T> OnOwnThread<T>(Func<T> body) {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { done.SetResult(body()); } catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        return done.Task;
    }

    [Test]
    public async Task A_second_launch_is_refused_and_asks_the_running_copy_to_show() {
        var name = UniqueName();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var held = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        var owner = OnOwnThread(() => {
            var guard = WindowsSingleInstanceLock.TryAcquire(name, TimeSpan.Zero);
            if (guard is not null) guard.ShowRequested += () => shown.TrySetResult();
            held.Set();
            exit.Wait(); // a thread that ends abandons the mutex, which would free it
            return guard;
        });
        held.Wait();

        var second = await OnOwnThread(() => WindowsSingleInstanceLock.TryAcquire(name, TimeSpan.Zero));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        exit.Set();
        using var first = await owner;

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task A_relaunch_waits_for_the_old_copy_to_exit_and_then_takes_the_lock() {
        var name = UniqueName();
        using var held = new ManualResetEventSlim();
        using var exit = new ManualResetEventSlim();
        var oldCopy = OnOwnThread(() => {
            var guard = WindowsSingleInstanceLock.TryAcquire(name, TimeSpan.Zero);
            held.Set();
            exit.Wait();
            return guard; // the thread ends still owning the mutex, as a process exit does
        });
        held.Wait();

        var relaunch = OnOwnThread(() => WindowsSingleInstanceLock.TryAcquire(name, TimeSpan.FromSeconds(10)));
        await Task.Delay(200);
        await Assert.That(relaunch.IsCompleted).IsFalse();
        exit.Set();

        using var old = await oldCopy;
        await Assert.That(old).IsNotNull();
        using var next = await relaunch.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(next).IsNotNull();
    }

    [Test]
    public async Task The_lock_name_ignores_path_case_and_carries_no_backslash() {
        var lower = WindowsSingleInstanceLock.NameFor(@"C:\Users\someone\AppData\Local\kcap\current\kcap-desktop.exe");
        var upper = WindowsSingleInstanceLock.NameFor(@"C:\USERS\SOMEONE\APPDATA\LOCAL\KCAP\CURRENT\KCAP-DESKTOP.EXE");
        var other = WindowsSingleInstanceLock.NameFor(@"C:\src\kcap-cli\src\Capacitor.App\bin\Debug\net10.0\kcap-desktop.exe");

        await Assert.That(upper).IsEqualTo(lower);
        await Assert.That(other).IsNotEqualTo(lower);
        await Assert.That(lower).DoesNotContain("\\");
    }
}

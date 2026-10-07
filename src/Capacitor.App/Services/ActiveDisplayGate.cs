namespace Capacitor.App.Services;

/// <summary>
/// Holds the launch until a display is active. Avalonia.Native registers a CVDisplayLink while it
/// builds its compositor, CoreVideo refuses one while every display is asleep (-6661), and Avalonia's
/// setup is one-shot per process, so a launch into a dark screen can only wait here, ahead of it.
/// The wait is unbounded: an app relaunched while the Mac sits locked overnight must still be there
/// in the morning, and waking the display on the user's behalf is not this process's call.
/// </summary>
public static class ActiveDisplayGate {
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <param name="activeDisplays">The active display count. A negative value means the query failed,
    /// and the launch proceeds rather than wait on a signal it cannot read.</param>
    /// <param name="onWaiting">Raised once, when the first poll finds no display.</param>
    public static async Task WaitAsync(Func<int> activeDisplays, TimeProvider time, Action onWaiting) {
        if (activeDisplays() != 0) return;

        onWaiting();
        do {
            await Task.Delay(PollInterval, time);
        } while (activeDisplays() == 0);
    }
}

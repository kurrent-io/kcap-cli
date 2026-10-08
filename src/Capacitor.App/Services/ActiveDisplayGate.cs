namespace Capacitor.App.Services;

/// <summary>
/// Holds the launch until CoreVideo will hand out a display link. Avalonia.Native registers one
/// while it builds its compositor, CoreVideo refuses it while every display is asleep (-6661), and
/// Avalonia's setup is one-shot per process, so a launch into a dark screen can only wait here,
/// ahead of it. The wait is unbounded: an app relaunched while the Mac sits locked overnight must
/// still be there in the morning, and waking the display on the user's behalf is not this
/// process's call.
/// </summary>
public static class ActiveDisplayGate {
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <param name="probe">CoreVideo's code for creating a display link now: 0 proceeds, any other code
    /// waits, and null means the probe could not run, so the launch proceeds rather than wait on a
    /// signal it cannot read.</param>
    /// <param name="onWaiting">Raised once, when the first probe is refused.</param>
    public static async Task WaitAsync(Func<int?> probe, TimeProvider time, Action onWaiting) {
        if (!Refused(probe())) return;

        onWaiting();
        do {
            await Task.Delay(PollInterval, time);
        } while (Refused(probe()));
    }

    static bool Refused(int? code) => code is not (null or 0);
}

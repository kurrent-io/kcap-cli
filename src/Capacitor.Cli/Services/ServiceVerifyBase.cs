namespace Capacitor.Cli.Services;

/// <summary>The timings and stderr reporting both <c>--verify</c> engines share.</summary>
abstract class ServiceVerifyBase {
    /// <summary>How long an engine polls for the daemon to answer. The desktop app's mutation timeout is
    /// sized above <see cref="LaunchdServiceVerify.AdvertisedBound"/>, which includes this, so a value per
    /// engine could outgrow that timeout on one platform unnoticed.</summary>
    protected static readonly TimeSpan ForwardBudget = TimeSpan.FromSeconds(20);

    protected static readonly TimeSpan LockWait     = TimeSpan.FromSeconds(10);
    protected static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    protected static readonly TimeSpan KillWait     = TimeSpan.FromSeconds(5);
    protected static readonly TimeSpan FenceWait    = TimeSpan.FromSeconds(3);

    // The app reads these lines; a broken pipe on one must not turn a settled outcome into a crash.
    protected static void Say(string line) {
        try { Console.Error.WriteLine(line); } catch (IOException) { }
    }
}

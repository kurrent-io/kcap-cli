namespace Capacitor.Cli.Core.Resume;

/// <summary>How long an attached terminal waits for its agent to come back after a daemon restart.</summary>
public static class AttachReconnectWindow {
    public const string EnvVar = "KCAP_ATTACH_RECONNECT_SECONDS";

    const int DefaultSeconds = 90;

    public static TimeSpan Resolve() =>
        TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable(EnvVar), out var seconds) && seconds > 0 ? seconds : DefaultSeconds);
}

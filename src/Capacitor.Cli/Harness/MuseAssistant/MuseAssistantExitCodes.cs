namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>What the cron keys on: 0 keeps nothing, 1 and 2 keep the directory for a retry, 3 says the
/// chat needs a grant before any retry can help.</summary>
public static class MuseAssistantExitCodes {
    public const int Ok         = 0;
    public const int Server     = 1;
    public const int Malformed  = 2;
    public const int OutOfScope = 3;
}

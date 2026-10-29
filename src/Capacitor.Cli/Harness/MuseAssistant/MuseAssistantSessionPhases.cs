namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>Where an open session's lifecycle stands on the server as far as this machine knows. A
/// pending phase is an intent written before the post, so a rerun posts exactly what the first attempt
/// would have.</summary>
public static class MuseAssistantSessionPhases {
    public const string PendingStart = "pending_start";
    public const string Started      = "started";
    public const string PendingEnd   = "pending_end";
}

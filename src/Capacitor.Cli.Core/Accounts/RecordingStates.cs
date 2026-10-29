namespace Capacitor.Cli.Core.Accounts;

/// <summary>The snake_case names a <see cref="RecordingState"/> travels under in
/// <see cref="AccountViewDto"/>.</summary>
public static class RecordingStates {
    public static string Token(RecordingState state) => state switch {
        RecordingState.Recording => "recording",
        RecordingState.Installed => "installed",
        RecordingState.Broken    => "broken",
        RecordingState.NotWired  => "not_wired",
    };
}

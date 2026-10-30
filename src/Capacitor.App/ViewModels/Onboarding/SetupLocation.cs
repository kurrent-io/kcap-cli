namespace Capacitor.App.ViewModels.Onboarding;

/// Where the wizard tells a user to find setup again. Only macOS draws an application menu; elsewhere
/// the Help and support flyout is the one entry. Coding tools are connected later from Settings → Harnesses,
/// so a user is not sent back through the whole wizard for one.
public static class SetupLocation {
    public static string Path { get; } = OperatingSystem.IsMacOS()
        ? "the application menu → Set up kcap"
        : "Help and support → Set up kcap";

    public const string HarnessesSettings = "Settings → Harnesses";

    public static string NoneFoundTail { get; } =
        $". Install a supported tool, then open {HarnessesSettings} after setup to rescan and turn on recording.";

    public static string ConnectLaterNote { get; } = $"You can connect coding tools later in {HarnessesSettings}.";

    public static string NothingSelectedNote { get; } = $"No new connections selected. {ConnectLaterNote}";

    public static string DisconnectLaterNote { get; } = $"These connection choices are saved. You can turn a harness off later in {HarnessesSettings}.";

    public static string ReopenAnytimeNote { get; } = $"You can reopen setup anytime from {Path}.";

    public static string FinishLaterTip { get; } = $"Continue setup from {Path}";
}

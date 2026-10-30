namespace Capacitor.App.ViewModels.Onboarding;

/// Where the wizard tells a user to find setup again. Only macOS draws an application menu; elsewhere
/// the Help and support flyout is the one entry.
public static class SetupLocation {
    public static string Path { get; } = OperatingSystem.IsMacOS()
        ? "the kcap menu → Set up kcap"
        : "Help and support → Set up kcap";

    /// Setup runs on launch, so a relaunch is how a user with nothing to connect comes back to it.
    public static string Relaunch { get; } = OperatingSystem.IsMacOS()
        ? "relaunch kcap from the Applications folder"
        : "relaunch the application";

    public static string NoneFoundTail { get; } = $". Install a harness, then {Relaunch}";

    public static string NothingSelectedNote { get; } =
        $"No new connections selected. Reopen setup from {Path} whenever you're ready.";

    public static string ReopenAnytimeNote { get; } = $"You can reopen setup anytime from {Path}.";

    public static string FinishLaterTip { get; } = $"Continue setup from {Path}";
}

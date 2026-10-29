namespace Capacitor.App.ViewModels.Onboarding;

/// Where the wizard tells a user to find setup again. Only macOS draws an application menu; elsewhere
/// the Help and support flyout is the one entry.
public static class SetupLocation {
    public static string Path { get; } = OperatingSystem.IsMacOS()
        ? "the application menu → Set up kcap"
        : "Help and support → Set up kcap";

    public static string NoneFoundTail { get; } = $". Install a harness, then reopen setup from {Path}.";

    public static string NothingSelectedNote { get; } =
        $"No new connections selected. Reopen setup from {Path} whenever you're ready.";

    public static string ReopenAnytimeNote { get; } = $"You can reopen setup anytime from {Path}.";

    public static string FinishLaterTip { get; } = $"Continue setup from {Path}";
}

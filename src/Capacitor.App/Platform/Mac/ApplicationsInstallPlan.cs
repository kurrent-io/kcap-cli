namespace Capacitor.App.Platform.Mac;

public enum ApplicationsInstallAction { Install, Update, OpenInstalled, Blocked }

public sealed record ApplicationsInstallPlan(
    ApplicationsInstallAction Action, string Target, string? SourceVersion = null,
    string? InstalledVersion = null, string? Error = null, string? ExistingPath = null, string? PayloadPath = null) {
    public string Title => Action switch {
        ApplicationsInstallAction.Update => "Update kcap",
        ApplicationsInstallAction.OpenInstalled => "kcap is already installed",
        ApplicationsInstallAction.Blocked => "Cannot install kcap",
        _ => "Install kcap",
    };

    public string ButtonLabel => Action switch {
        ApplicationsInstallAction.Update => "Quit and update",
        ApplicationsInstallAction.OpenInstalled => "Open installed copy",
        _ => "Install and open",
    };

    public string Detail => Action switch {
        ApplicationsInstallAction.Update => "kcap will be updated and reopened."
            + " Your settings and sign-in will stay intact.\n\n"
            + "The app will quit and its daemon will restart. This interrupts active turns; resumable agents restart unless auto-resume is off.",
        ApplicationsInstallAction.OpenInstalled => "The installed copy of kcap will open. Your settings and sign-in will stay intact.",
        ApplicationsInstallAction.Blocked => Error ?? "The existing copy cannot be safely replaced.",
        _ => "kcap will be installed in Applications and open automatically. Your settings and sign-in will stay intact.",
    };
}

namespace Capacitor.App.Services;

public enum ApplicationsInstallAction { Install, Update, OpenInstalled, Blocked }

public sealed record ApplicationsInstallPlan(
    ApplicationsInstallAction Action, string Target, string? SourceVersion = null,
    string? InstalledVersion = null, string? Error = null, string? ExistingPath = null, string? PayloadPath = null) {
    public bool Migrates => ExistingPath is not null && ExistingPath != Target;
    public string Title => Action switch {
        ApplicationsInstallAction.Update => "Update kcap",
        ApplicationsInstallAction.OpenInstalled => "kcap is already installed",
        ApplicationsInstallAction.Blocked => "Choose another location",
        _ => "Move to Applications",
    };

    public string ButtonLabel => Action switch {
        ApplicationsInstallAction.Update => "Quit and update",
        ApplicationsInstallAction.OpenInstalled => "Open installed copy",
        ApplicationsInstallAction.Blocked => "Choose a destination",
        _ => "Move and open",
    };

    public string Detail => Action switch {
        ApplicationsInstallAction.Update => $"{(Migrates ? "Replace Kurrent Capacitor with kcap" : "Update kcap")} at {Target}.\n\n"
            + "The app will quit and its daemon will restart. This interrupts active turns; resumable agents restart unless auto-resume is off.\n\n"
            + "Your settings and sign-in will stay intact.",
        ApplicationsInstallAction.OpenInstalled => $"Use the installed copy at {Target}.\n\nYour settings and sign-in will stay intact.",
        ApplicationsInstallAction.Blocked => Error ?? "The existing copy cannot be safely replaced.",
        _ => "Its command-line tool and background service need a permanent location.\n\nYour settings and sign-in will stay intact.",
    };
}

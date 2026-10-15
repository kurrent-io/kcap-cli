namespace Capacitor.App.Services;

public enum ApplicationsInstallAction { Install, Update, OpenInstalled, Blocked }

public sealed record ApplicationsInstallPlan(
    ApplicationsInstallAction Action, string Target, string? SourceVersion = null,
    string? InstalledVersion = null, string? Error = null) {
    public string Title => Action switch {
        ApplicationsInstallAction.Update => "Update Capacitor",
        ApplicationsInstallAction.OpenInstalled => "Capacitor is already installed",
        _ => "Move to Applications",
    };

    public string ButtonLabel => Action switch {
        ApplicationsInstallAction.Update => "Update and open",
        ApplicationsInstallAction.OpenInstalled => "Open installed copy",
        _ => "Move and open",
    };

    public string Detail => Action switch {
        ApplicationsInstallAction.Update => $"Replace version {InstalledVersion} with {SourceVersion}. Your settings and sign-in will stay intact.",
        ApplicationsInstallAction.OpenInstalled => "Use the copy in Applications. Your settings and sign-in will stay intact.",
        ApplicationsInstallAction.Blocked => Error ?? "The existing copy cannot be safely replaced.",
        _ => "Its command-line tool and background service need a permanent location. Your settings and sign-in will stay intact.",
    };
}

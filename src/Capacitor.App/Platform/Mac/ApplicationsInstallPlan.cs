namespace Capacitor.App.Platform.Mac;

public enum ApplicationsInstallAction { Install, Update, OpenInstalled, Blocked }

/// <param name="Replaces">Other kcap bundles in an Applications folder that the installed copy supersedes,
/// the running one included when it is there: they are removed once the installed copy opens.</param>
public sealed record ApplicationsInstallPlan(
    ApplicationsInstallAction Action, string Target, string? SourceVersion = null,
    string? InstalledVersion = null, string? Error = null, string? ExistingPath = null, string? PayloadPath = null,
    IReadOnlyList<string>? Replaces = null) {
    const string RestartNote = "kcap will quit and the daemon will restart. Active turns stop. Eligible agents resume if auto-resume is on. Other sessions may need attention.";

    public IReadOnlyList<string> Replaces { get; init; } = Replaces ?? [];

    /// A pre-rebrand Kurrent Capacitor app is among what this install replaces: to the user, an upgrade to kcap.
    public bool Migrates => Action != ApplicationsInstallAction.Blocked
        && Replaces.Any(root => Path.GetFileName(root) == KcapBundle.LegacyBundleName);

    /// The installed copy opens and replaces another installed copy of kcap, not a pre-rebrand one.
    bool KeepsOne => Action == ApplicationsInstallAction.OpenInstalled && Replaces.Count > 0 && !Migrates
        && Replaces.All(root => Path.GetFileName(root) == KcapBundle.BundleName);

    /// Keeping <see cref="Target"/> removes the kcap.app in every other one of <paramref name="directories"/>; it
    /// cannot when another copy is newer, because keeping one copy never downgrades the other.
    public bool RetiresOthersIn(IEnumerable<string> directories) =>
        Action != ApplicationsInstallAction.Blocked
        && directories.Select(directory => Path.GetFullPath(Path.Combine(directory, KcapBundle.BundleName)))
            .Where(root => root != Path.GetFullPath(Target))
            .All(root => Replaces.Any(replaced => Path.GetFullPath(replaced) == root));

    public string Title => Action switch {
        ApplicationsInstallAction.Blocked => "Cannot install kcap",
        _ when KeepsOne => "kcap is installed twice",
        _ when Migrates => "Upgrade to kcap",
        ApplicationsInstallAction.Update => "Update kcap",
        ApplicationsInstallAction.OpenInstalled => "kcap is already installed",
        _ => "Install kcap",
    };

    public string ButtonLabel => Action switch {
        ApplicationsInstallAction.Blocked => "Show in Finder",
        ApplicationsInstallAction.OpenInstalled when Migrates => "Replace and open",
        _ when KeepsOne => "Keep this copy",
        _ when Migrates => "Quit and update",
        ApplicationsInstallAction.Update => "Quit and update",
        ApplicationsInstallAction.OpenInstalled => "Open installed copy",
        _ => "Install and open",
    };

    public string Detail => Action switch {
        ApplicationsInstallAction.OpenInstalled when Migrates =>
            $"kcap at {Target} will replace Kurrent Capacitor and open. Settings and sign-in are kept.\n\n{RestartNote}",
        _ when Migrates =>
            $"kcap will replace Kurrent Capacitor and open. Settings and sign-in are kept.\n\n{RestartNote}",
        ApplicationsInstallAction.Update => $"kcap will update and reopen.{Removal} Settings and sign-in are kept.\n\n{RestartNote}",
        _ when KeepsOne =>
            $"The copy at {Target} stays. {(Replaces.Count == 1 ? $"The one at {Replaces[0]} is removed" : "The other copies are removed")}. The daemon and terminal command will use the remaining copy.\n\n{RestartNote}",
        ApplicationsInstallAction.OpenInstalled when Replaces.Count == 0 =>
            "The installed copy of kcap will open. Settings and sign-in are kept.",
        ApplicationsInstallAction.OpenInstalled =>
            $"The installed copy at {Target} will open.{Removal} Settings and sign-in are kept.\n\n{RestartNote}",
        ApplicationsInstallAction.Blocked => Error ?? "The existing copy cannot be safely replaced.",
        _ when Replaces.Count > 0 =>
            $"kcap will install at {Target}. It opens automatically.{Removal} Settings and sign-in are kept.\n\n{RestartNote}",
        _ => $"kcap will install at {Target}. It opens automatically. Settings and sign-in are kept.",
    };

    string Removal => Replaces.Count == 0 ? ""
        : Replaces.Count == 1 ? $" It replaces the copy at {Replaces[0]}."
        : " It replaces the other copies in the Applications folders.";

    public bool Equals(ApplicationsInstallPlan? other) =>
        other is not null && Action == other.Action && Target == other.Target && SourceVersion == other.SourceVersion
        && InstalledVersion == other.InstalledVersion && Error == other.Error && ExistingPath == other.ExistingPath
        && PayloadPath == other.PayloadPath && Replaces.SequenceEqual(other.Replaces);

    public override int GetHashCode() => HashCode.Combine(Action, Target, SourceVersion, InstalledVersion, ExistingPath, PayloadPath, Replaces.Count);
}

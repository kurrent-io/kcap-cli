namespace Capacitor.App.Services;

/// Everything the controller shows a human. The Avalonia implementation
/// renders dialogs/status lines; tests fake it.
public interface ILifecycleSurface {
    /// Honest one-liners — the message lane (e.g. degraded-but-owned, coded-failure surfaces).
    void Status(string message);

    Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct);

    /// Like ConfirmAsync, but null distinguishes "ct won before the dialog factory ran" from a genuinely shown-and-declined dialog (false).
    Task<bool?> TryConfirmAsync(LifecyclePrompt prompt, CancellationToken ct);

    /// Repair-affordance surfaces — never a silent mutation.
    void Attention(string message);

    /// Takes back a Status or Attention line whose condition has cleared, if it is still the one shown.
    void Withdraw(string message) { }
}

/// <param name="Kind">One of the Kind* consts below.</param>
/// <param name="PathDegraded">Disclosed when the terminal PATH is unknown.</param>
/// <param name="Disclosure">Replacement/recapture text.</param>
/// <param name="Warning">A distinct data-loss line shown beneath the disclosure.</param>
public sealed record LifecyclePrompt(
    string Kind, string? DaemonVersion, string? CliVersion, bool PathDegraded, string Disclosure, string? Warning = null) {
    public const string KindTakeover      = "takeover";
    public const string KindRepair        = "repair";
    public const string KindShim          = "shim";
    public const string KindQuarantine    = "quarantine";
    public const string KindUpdateReady   = "update-ready";
    public const string KindUpdateInfo    = "update-info";
    public const string KindReloadService = "reload-service";
    public const string KindRename        = "rename";
    public const string KindRemoveProfile = "remove-profile";
    public const string KindSetup         = "setup";
    public const string KindUninstall     = "uninstall";
    public const string KindWindowsService       = "windows-service";
    public const string KindWindowsServiceUpdate = "windows-service-update";
}

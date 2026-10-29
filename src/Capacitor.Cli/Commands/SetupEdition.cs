using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>The terminal's copy of the desktop app's first screen, with its labels and its telemetry values.</summary>
static class SetupEdition {
    public enum Choice { Local, Cloud, Existing }

    public const string LocalLabel    = "For you, locally";
    public const string CloudLabel    = "For your team, cloud";
    public const string ExistingLabel = "Already have a workspace?";

    public static string Label(Choice choice) => choice switch {
        Choice.Local => LocalLabel,
        Choice.Cloud => CloudLabel,
        _            => ExistingLabel,
    };

    /// <summary>The value <c>cli_setup_edition_chosen</c> carries, the same three the app sends.</summary>
    public static string Wire(Choice choice) => choice switch {
        Choice.Local => "local",
        Choice.Cloud => "cloud",
        _            => "existing_workspace",
    };

    /// <summary>kcap-web's sign-up, on the Team plan: creating a cloud workspace is kcap-web's job end to end.</summary>
    public static string SignUpUrl(AuthEndpoints endpoints) => $"{endpoints.SignupUrl.TrimEnd('/')}/get-started?tier=team";
}

using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.Services;

/// How an account's recording-state token reads in the launcher and the Settings tab. Codex
/// reports "installed" until the user trusts the kcap hooks on the next start, which is why the
/// same token reads differently per vendor.
public static class RecordingStateLabels {
    static readonly string Recording = RecordingStates.Token(RecordingState.Recording);
    static readonly string Installed = RecordingStates.Token(RecordingState.Installed);
    static readonly string Broken    = RecordingStates.Token(RecordingState.Broken);
    static readonly string NotWired  = RecordingStates.Token(RecordingState.NotWired);

    public static string Label(string vendor, string token) =>
        token == Recording ? "Recording"
        : token == Installed ? (IsCodex(vendor) ? "Needs approval in Codex" : "Ready")
        : token == Broken ? "Needs repair"
        : token == NotWired ? "Not set up"
        : token;

    /// Nothing for the user to do: recording, or hooks in place waiting for the first session.
    public static bool IsHealthy(string vendor, string token) =>
        token == Recording || (token == Installed && !IsCodex(vendor));

    public static bool IsAttention(string vendor, string token) =>
        token == Broken || token == NotWired || (token == Installed && IsCodex(vendor));

    public static string? Hint(string vendor, string token, string directory) =>
        token == Installed && IsCodex(vendor)
            ? "Start Codex once with this account and approve kcap's hooks when it asks."
            : token == Broken ? "kcap's hooks in this folder are missing or changed. Reconnect from the menu."
            : null;

    /// The command that does what the hint asks, when there is one to run.
    public static string? Command(string vendor, string token, string directory) =>
        Command(vendor, token, directory, OperatingSystem.IsWindows());

    /// PowerShell sets a variable with $env:, and a POSIX shell takes it as a prefix; either way the
    /// folder is quoted when it holds anything a shell would read as syntax.
    public static string? Command(string vendor, string token, string directory, bool windows) =>
        token != Installed || !IsCodex(vendor) ? null
        : windows ? $"$env:CODEX_HOME = '{directory.Replace("'", "''", StringComparison.Ordinal)}'; codex"
        : $"CODEX_HOME={PosixWord(directory)} codex";

    static string PosixWord(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '~' or '+' or ',' or ':' or '@')
            ? value
            : $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    static bool IsCodex(string vendor) => string.Equals(vendor, "codex", StringComparison.OrdinalIgnoreCase);
}

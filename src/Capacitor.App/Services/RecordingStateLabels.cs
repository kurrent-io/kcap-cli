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
        : token == Installed ? (IsCodex(vendor) ? "Needs trust" : "Wired")
        : token == Broken ? "Broken"
        : token == NotWired ? "Not wired"
        : token;

    public static bool IsAttention(string vendor, string token) =>
        token == Broken || token == NotWired || (token == Installed && IsCodex(vendor));

    public static string? Hint(string vendor, string token, string directory) =>
        token == Installed && IsCodex(vendor)
            ? $"Trust the kcap hooks the next time you start Codex with CODEX_HOME={directory}"
            : null;

    static bool IsCodex(string vendor) => string.Equals(vendor, "codex", StringComparison.OrdinalIgnoreCase);
}

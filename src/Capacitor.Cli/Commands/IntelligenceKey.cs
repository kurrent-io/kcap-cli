using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Commands;

/// <summary>Where a provider's API key comes from: its own environment variable, else a hidden prompt. Never the
/// command line, which any local user can read.</summary>
static class IntelligenceKey {
    /// <param name="prompt">Asks for the key without echoing it; null where nobody can be asked.</param>
    public static (string? Key, string? Error) Resolve(IntelligenceVendor vendor, Func<string, string?> variable, Func<string?>? prompt) {
        if (variable(vendor.KeyVariable)?.Trim() is { Length: > 0 } fromEnvironment) return (fromEnvironment, null);

        if (prompt?.Invoke()?.Trim() is { Length: > 0 } typed) return (typed, null);

        return (null, $"{vendor.Label} needs an API key: set {vendor.KeyVariable}, or run this where it can ask for one.");
    }
}

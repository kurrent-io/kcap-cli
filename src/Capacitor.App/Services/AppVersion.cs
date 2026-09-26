using System.Reflection;

namespace Capacitor.App.Services;

public static class AppVersion {
    public static string Display { get; } = Format(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// Drops the `+commit` build metadata MinVer appends.
    public static string Format(string? informational) {
        if (string.IsNullOrWhiteSpace(informational)) return "unknown";
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}

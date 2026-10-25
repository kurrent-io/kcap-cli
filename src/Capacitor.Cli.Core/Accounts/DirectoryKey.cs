namespace Capacitor.Cli.Core.Accounts;

public static class DirectoryKey {
    // Lower-cased off Linux so a reader on another machine matches with an ordinal compare.
    public static string Of(string directory) {
        var normalized = AccountDirectory.Normalize(directory);

        return OperatingSystem.IsLinux() ? normalized : normalized.ToLowerInvariant();
    }
}

namespace Capacitor.Cli.Core.Accounts;

public static class AccountDirectory {
    public static string Normalize(string path) {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try {
            if (new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                return Path.TrimEndingDirectorySeparator(target.FullName);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return full;
    }

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

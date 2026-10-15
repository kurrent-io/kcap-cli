namespace Capacitor.Cli.Core.Accounts;

public static class AccountDirectory {
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(PhysicalPath.Of(path));

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}

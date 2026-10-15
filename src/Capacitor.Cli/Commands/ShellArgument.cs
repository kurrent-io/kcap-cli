namespace Capacitor.Cli.Commands;

/// <summary>A path printed inside a command the user is meant to paste into their shell.</summary>
internal static class ShellArgument {
    public static string Quote(string value) => Quote(value, OperatingSystem.IsWindows());

    public static string Quote(string value, bool windows) {
        if (value.Length > 0 && value.All(c => IsSafe(c) || (windows && c == '\\'))) return value;

        return windows ? $"\"{value}\"" : "'" + value.Replace("'", "'\\''") + "'";
    }

    static bool IsSafe(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ':' or '+' or ',' or '@' or '=';
}

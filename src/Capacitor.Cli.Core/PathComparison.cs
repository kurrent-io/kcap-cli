namespace Capacitor.Cli.Core;

/// <summary>
/// How two paths are compared or grouped. Windows and macOS default to case-insensitive
/// filesystems, where an ordinal comparison reads one directory reached through two casings as two
/// — enough to turn a differently cased launch into an anchor move and settle a deletion against
/// what the same run has just published.
///
/// <para>The choice is per platform rather than per volume, so it is wrong the other way on a
/// case-sensitive volume on those two: a macOS volume created case-sensitive, or a Windows
/// directory flagged so, has two directories differing only in case read as one.</para>
/// </summary>
public static class PathComparison {
    static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparison Comparison { get; } =
        IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } =
        IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool Equal(string? left, string? right) => string.Equals(left, right, Comparison);
}

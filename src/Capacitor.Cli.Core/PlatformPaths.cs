namespace Capacitor.Cli.Core;

/// One platform path rule for every surface: case-insensitive on Windows and macOS, case-sensitive
/// on Linux, and a trailing directory separator never distinguishes two paths.
public static class PlatformPaths {
    public static readonly StringComparer Comparer = new TrailingSeparatorComparer(
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string path) =>
        string.IsNullOrEmpty(path) ? path : Path.TrimEndingDirectorySeparator(path);

    public static string Leaf(string path) => Path.GetFileName(Normalize(path));

    sealed class TrailingSeparatorComparer(StringComparer inner) : StringComparer {
        public override int Compare(string? x, string? y) {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            return inner.Compare(Normalize(x), Normalize(y));
        }

        public override bool Equals(string? x, string? y) {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return inner.Equals(Normalize(x), Normalize(y));
        }

        public override int GetHashCode(string obj) => inner.GetHashCode(Normalize(obj));
    }
}

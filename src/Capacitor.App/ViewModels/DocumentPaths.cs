namespace Capacitor.App.ViewModels;

/// Paths from two machines: a declared repo-relative path and a working-copy or card path. They
/// match when one ends with the other on a segment boundary, whichever separator either uses.
public static class DocumentPaths {
    public static bool Match(string a, string b) {
        var left = Normalise(a);
        var right = Normalise(b);
        if (left.Length == 0 || right.Length == 0) return false;
        if (left == right) return true;
        return left.EndsWith("/" + right, StringComparison.Ordinal) || right.EndsWith("/" + left, StringComparison.Ordinal);
    }

    static string Normalise(string path) => path.Replace('\\', '/').TrimEnd('/');
}

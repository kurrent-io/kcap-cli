namespace Capacitor.Tests.Helpers;

/// Reads a file a live writer still owns. <c>File.ReadAllText</c> and friends open
/// <c>FileShare.Read</c>, which DENIES Write to every other handle — mandatory on Windows, so such a
/// read stops the writer appending to its own journal while the test looks at it.
public static class JournalFiles {
    public static string[] ReadLines(string path) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    public static string ReadText(string path) => string.Join("\n", ReadLines(path));
}

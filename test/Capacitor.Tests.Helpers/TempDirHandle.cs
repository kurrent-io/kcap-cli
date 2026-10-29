using System.Diagnostics;
using System.Runtime.Versioning;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A directory that makes its own children — what <see cref="TempDir.CreateDir"/> returns. Converts
/// implicitly to its path, so it still goes anywhere the path string did. Owns nothing; the
/// <see cref="TempDir"/> it came from deletes the tree. The public constructor does not create.
///
/// <para>Pass <see cref="Path"/> explicitly to generic APIs: inference beats a user-defined conversion,
/// so <c>JsonValue.Create(dir)</c> compiles and serialises the struct instead of the path.</para>
/// </summary>
public readonly record struct TempDirHandle(string Path) {
    public static implicit operator string(TempDirHandle dir) => dir.Path;

    public override string ToString() => Path;

    /// <summary>Path of an entry under this directory. Nothing is created.</summary>
    public string PathTo(params ReadOnlySpan<string> segments) {
        var parts = new string[segments.Length + 1];

        parts[0] = Path;
        segments.CopyTo(parts.AsSpan(1));

        return System.IO.Path.Combine(parts);
    }

    /// <summary>Creates a subdirectory (and any missing parents) and returns it.</summary>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) =>
        new(Directory.CreateDirectory(PathTo(segments)).FullName);

    /// <summary>Creates a chain of <paramref name="depth"/> nested directories and returns the
    /// deepest — for a test that needs a path with many components. Each segment is one character,
    /// so a deep chain still fits inside Windows' 260-character classic path limit.</summary>
    public TempDirHandle Nest(int depth) {
        var dir = this;

        for (var i = 0; i < depth; i++) dir = dir.CreateDir("d");

        return dir;
    }

    /// <summary>Writes a file, creating any missing parent directories, and returns its path.</summary>
    public string CreateFile(string relativePath, string content = "") =>
        Write(PathTo(relativePath), content);

    /// <summary>As <see cref="CreateFile(string,string)"/>, from path segments:
    /// <c>dir.CreateFile(["events", "events.jsonl"], body)</c>.</summary>
    public string CreateFile(ReadOnlySpan<string> segments, string content = "") =>
        Write(PathTo(segments), content);

    /// <summary>As <see cref="CreateFile(string,string)"/> for line-oriented content:
    /// <c>dir.CreateFile("events.jsonl", [lineA, lineB])</c>.</summary>
    // WriteAllLines, not a join: it terminates the last line, which the JSONL readers require.
    public string CreateFile(string relativePath, string[] lines) {
        var path = PathTo(relativePath);

        EnsureParent(path);
        File.WriteAllLines(path, lines);

        return path;
    }

    /// <summary>Writes a file the test will run, owner-executable, and returns its path. A child
    /// process writes the bytes: a write handle opened here is copied into whatever a concurrent
    /// test forks, and until that child execs Linux refuses to run the file with ETXTBSY — which
    /// code that swallows a failed spawn reports as an ordinary miss. The content travels as one
    /// argument, so it is bounded by the OS limit on one (128 KiB on Linux).</summary>
    public string CreateExecutable(string relativePath, string content) {
        var path = PathTo(relativePath);

        if (OperatingSystem.IsWindows()) return Write(path, content);

        // printf is a shell builtin, so this holds whatever the test has done to PATH.
        return WriteFromChild(path, "printf %s \"$2\" > \"$1\"", content);
    }

    /// <summary>As <see cref="CreateExecutable"/>, for a copy of an existing binary.</summary>
    public string CopyExecutable(string source, string relativePath) {
        var path = PathTo(relativePath);

        if (!OperatingSystem.IsWindows()) return WriteFromChild(path, "/bin/cp \"$2\" \"$1\"", source);

        EnsureParent(path);
        File.Copy(source, path);

        return path;
    }

    [UnsupportedOSPlatform("windows")]
    static string WriteFromChild(string path, string command, string operand) {
        EnsureParent(path);

        using var writer = Process.Start(new ProcessStartInfo("/bin/sh") {
            UseShellExecute = false,
            ArgumentList    = { "-c", command, "sh", path, operand }
        }) ?? throw new InvalidOperationException($"Could not start /bin/sh to write {path}.");

        writer.WaitForExit();

        if (writer.ExitCode != 0) throw new IOException($"Writing {path} exited {writer.ExitCode}.");

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        return path;
    }

    static string Write(string path, string content) {
        EnsureParent(path);
        File.WriteAllText(path, content);

        return path;
    }

    static void EnsureParent(string path) {
        var dir = System.IO.Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}

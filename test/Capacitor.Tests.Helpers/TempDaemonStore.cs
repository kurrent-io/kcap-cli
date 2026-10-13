using System.Runtime.CompilerServices;
using Capacitor.Cli.Core;

namespace Capacitor.Tests.Helpers;

/// <summary>A <see cref="DaemonStore"/> over a throwaway directory, deleted on dispose — the
/// isolation unit for anything touching daemon lock/pid/marker/socket files.</summary>
public sealed class TempDaemonStore : IDisposable {
    // A control socket binds in here and macOS caps sockaddr_un at 103 chars, so the name needs room.
    const int HintLength = 6;

    // The daemons directory is a child so siblings derived from it (the account registry) land
    // inside this fixture instead of in the shared temp root.
    const string DaemonsSubdir = "d";

    readonly TempDir _dir;

    public DaemonStore Store { get; }

    public string Directory => Store.Directory;

    /// <summary>The fixture's own directory, the parent of <see cref="Directory"/>.</summary>
    public string Root => _dir.Path;

    /// <param name="hint">Names the directory instead of the caller's file.</param>
    public TempDaemonStore(string? hint = null, [CallerFilePath] string callerFilePath = "") {
        _dir  = new TempDir(Cut(hint ?? callerFilePath));
        Store = new DaemonStore(_dir.CreateDir(DaemonsSubdir).Path);
    }

    /// <summary>A path inside the daemons directory.</summary>
    public string PathTo(params ReadOnlySpan<string> segments) => _dir.PathTo(Under(segments));

    /// <summary>Creates a directory inside the daemons directory.</summary>
    public TempDirHandle CreateDir(params ReadOnlySpan<string> segments) => _dir.CreateDir(Under(segments));

    /// <summary>Creates a file inside the daemons directory.</summary>
    public string CreateFile(string relativePath, string content = "") =>
        _dir.CreateFile(System.IO.Path.Combine(DaemonsSubdir, relativePath), content);

    public void Dispose() => _dir.Dispose();

    // Enough of the suite name to attribute a leak, within the socket budget.
    static string Cut(string fileOrClassName) =>
        new(TempDir.Stem(fileOrClassName).Where(char.IsAsciiLetterOrDigit).Take(HintLength).ToArray());

    static string[] Under(ReadOnlySpan<string> segments) => [DaemonsSubdir, .. segments];
}

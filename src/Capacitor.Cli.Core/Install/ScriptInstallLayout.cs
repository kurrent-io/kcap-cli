namespace Capacitor.Cli.Core.Install;

/// <summary>
/// The directory tree the script installer lays out: <c>versions/&lt;v&gt;/bin/kcap</c> per version, a
/// <c>current</c> link to the active one, and an <c>install.json</c> marker at the root.
///
/// <para>On macOS and Linux the process path resolves through <c>current</c> to the version directory,
/// so anything that persists a kcap path (a service unit, an MCP registration, the plugin path) must
/// go through <see cref="Stabilize(string)"/>: a versioned path keeps running the old build after an update.</para>
/// </summary>
public sealed record ScriptInstallLayout(string Root) {
    public const string MarkerFileName = "install.json";
    public const string CurrentName    = "current";
    public const string VersionsName   = "versions";

    public string Marker   => Path.Combine(Root, MarkerFileName);
    public string Current  => Path.Combine(Root, CurrentName);
    public string Versions => Path.Combine(Root, VersionsName);

    public string VersionDir(string version) => Path.Combine(Versions, version);

    /// <summary>The path a file under <c>current/bin</c> is reached by, whichever version is active.</summary>
    public string CurrentBin(string fileName) => Path.Combine(Current, "bin", fileName);

    static readonly Lazy<ScriptInstallLayout?> Running = new(() => FromBinary(Environment.ProcessPath, File.Exists));

    /// <summary>The layout the running binary belongs to, or null when it is not a script install.</summary>
    public static ScriptInstallLayout? OfRunningBinary() => Running.Value;

    /// <summary>
    /// The layout a binary at <paramref name="binaryPath"/> belongs to: its directory is <c>bin</c>, under
    /// either <c>versions/&lt;v&gt;</c> or <c>current</c>, and the root carries the installer's marker.
    /// </summary>
    public static ScriptInstallLayout? FromBinary(string? binaryPath, Func<string, bool> fileExists) {
        if (string.IsNullOrEmpty(binaryPath)) return null;

        var bin = Path.GetDirectoryName(binaryPath);
        if (bin is null || Path.GetFileName(bin) != "bin") return null;

        var owner = Path.GetDirectoryName(bin);
        if (owner is null) return null;

        string? root;
        if (Path.GetFileName(owner) == CurrentName) {
            root = Path.GetDirectoryName(owner);
        } else {
            var versions = Path.GetDirectoryName(owner);
            root = versions is not null && Path.GetFileName(versions) == VersionsName
                ? Path.GetDirectoryName(versions)
                : null;
        }

        if (root is null) return null;

        var layout = new ScriptInstallLayout(root);

        return fileExists(layout.Marker) ? layout : null;
    }

    /// <summary>
    /// The <c>current/bin</c> form of a binary inside a script install's version directory, when that
    /// form exists; otherwise <paramref name="path"/> unchanged. A path outside any script install, or
    /// one already under <c>current</c>, is returned as given.
    /// </summary>
    public static string Stabilize(string path, Func<string, bool> fileExists) {
        var layout = FromBinary(path, fileExists);
        if (layout is null) return path;

        var stable = layout.CurrentBin(Path.GetFileName(path));

        return fileExists(stable) ? stable : path;
    }

    /// <summary><see cref="Stabilize(string, Func{string, bool})"/> against the real filesystem.</summary>
    public static string Stabilize(string path) => Stabilize(path, File.Exists);
}

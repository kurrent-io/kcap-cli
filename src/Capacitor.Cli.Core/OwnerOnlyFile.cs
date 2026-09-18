namespace Capacitor.Cli.Core;

/// <summary>A file that must not exist yet, readable by its owner only. CreateNew refuses a path
/// that already exists — including a symlink — so nothing is ever written through one.</summary>
public static class OwnerOnlyFile {
    public static FileStream CreateNew(string path) {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        return new FileStream(path, options);
    }
}

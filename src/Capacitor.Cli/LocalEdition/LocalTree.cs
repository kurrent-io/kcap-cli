namespace Capacitor.Cli.LocalEdition;

/// <summary>Removes a path under the local root without following a link: <c>bin/current</c> is a symlink on Unix and
/// a junction on Windows, where a recursive delete fails with access denied on a junction inside the tree and
/// <c>File.Delete</c> refuses one at the top.</summary>
static class LocalTree {
    /// <summary>False when nothing was there.</summary>
    public static bool Remove(string path) {
        var info = new DirectoryInfo(path);

        if (info.LinkTarget is not null) {
            if (OperatingSystem.IsWindows() && info.Exists) Directory.Delete(path);
            else File.Delete(path);
            return true;
        }

        if (File.Exists(path)) {
            File.Delete(path);
            return true;
        }

        if (!info.Exists) return false;

        if (OperatingSystem.IsWindows())
            foreach (var link in info.EnumerateDirectories("*", SearchOption.AllDirectories).Where(d => d.LinkTarget is not null).ToList())
                link.Delete();

        Directory.Delete(path, recursive: true);
        return true;
    }
}

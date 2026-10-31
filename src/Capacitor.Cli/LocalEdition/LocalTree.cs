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

        if (OperatingSystem.IsWindows()) RemoveLinks(info);

        Directory.Delete(path, recursive: true);
        return true;
    }

    /// <summary>Each link goes before anything descends into it, so a link out of the tree is never walked.</summary>
    static void RemoveLinks(DirectoryInfo directory) {
        foreach (var child in directory.EnumerateDirectories().ToList()) {
            if (child.LinkTarget is not null) child.Delete();
            else RemoveLinks(child);
        }
    }
}

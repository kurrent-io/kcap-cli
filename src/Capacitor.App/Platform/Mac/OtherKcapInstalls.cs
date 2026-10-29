namespace Capacitor.App.Platform.Mac;

/// Finds the `kcap` executables a terminal would reach that the uninstall will not remove.
public static class OtherKcapInstalls {
    const string NpmRemoveCommand = "npm uninstall -g @kurrent/kcap";

    public static IReadOnlyList<OtherKcapInstall> Find(string? terminalPath, string bundleRoot) {
        if (string.IsNullOrEmpty(terminalPath)) return [];

        var bundlePrefix = bundleRoot.TrimEnd('/') + "/";
        var seen         = new HashSet<string>(StringComparer.Ordinal);
        var found        = new List<OtherKcapInstall>();

        foreach (var dir in terminalPath.Split(':', StringSplitOptions.RemoveEmptyEntries)) {
            var candidate = System.IO.Path.Combine(dir, "kcap");
            if (!IsExecutableFile(candidate)) continue;

            var resolved = Resolve(candidate);
            if (resolved.StartsWith(bundlePrefix, StringComparison.Ordinal)) continue;
            if (!seen.Add(resolved)) continue;

            found.Add(new OtherKcapInstall(
                candidate,
                resolved.Contains("/node_modules/@kurrent/", StringComparison.Ordinal) ? NpmRemoveCommand : null));
        }

        return found;
    }

    static string Resolve(string path) {
        try {
            return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? System.IO.Path.GetFullPath(path);
        } catch (IOException) {
            return System.IO.Path.GetFullPath(path);
        }
    }

    static bool IsExecutableFile(string path) {
        try {
            return File.Exists(path)
                && !OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        } catch (IOException) {
            return false;
        }
    }
}

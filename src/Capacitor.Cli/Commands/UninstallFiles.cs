using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

static class UninstallFiles {
    // The local root retains its lifecycle lock and retired-origin registry. Deleting either
    // would let concurrent installers take different locks or reuse a retired endpoint.
    public static void DeleteConfig(string directory, string? localRoot) {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var keep = localRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(localRoot));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!CanonicalPath.TryResolve(path, out var physicalPath)
            || (keep is not null && !CanonicalPath.TryResolve(keep, out _)))
            throw new IOException("Cannot resolve the configuration and local installation paths safely.");
        var physicalKeep = keep is null ? null : CanonicalPath.Resolve(keep);
        if (physicalKeep is not null && physicalPath.Equals(physicalKeep, comparison)) return;
        if (keep is not null && physicalKeep!.StartsWith(physicalPath + Path.DirectorySeparatorChar, comparison)) {
            if (new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException($"Cannot remove linked config directory {path} while preserving the local installation.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) {
                if (Directory.Exists(entry)) DeleteConfig(entry, keep);
                else File.Delete(entry);
            }
            return;
        }
        Directory.Delete(path, recursive: true);
    }
}

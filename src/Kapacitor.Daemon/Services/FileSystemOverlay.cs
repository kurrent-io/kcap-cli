namespace kapacitor.Daemon.Services;

/// <summary>
/// Copies files from a source dir into a destination, creating directories
/// as needed but never overwriting existing files. Used by both
/// <see cref="ClaudeLauncher"/> and <see cref="CodexLauncher"/> to merge
/// vendor-specific dotfiles from the source repo into the worktree without
/// clobbering tracked content.
/// </summary>
internal static class FileSystemOverlay {
    public static void OverlayDirectory(string source, string dest) {
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.GetFiles(source)) {
            var destFile = Path.Combine(dest, Path.GetFileName(file));
            if (!File.Exists(destFile)) File.Copy(file, destFile);
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            OverlayDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}

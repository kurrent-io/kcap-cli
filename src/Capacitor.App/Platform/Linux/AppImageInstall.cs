using System.Security.Cryptography;
using Capacitor.Cli.Core;

namespace Capacitor.App.Platform.Linux;

/// The AppImage this process runs from. The image file outlives the process; the FUSE mount the app
/// runs inside does not, so nothing that must outlive the app may run from the mount: the CLI and
/// its plugin are copied out to a fixed directory, and a relaunch starts the image, not the mount.
public sealed record AppImageInstall(string Image) {
    static readonly string[] CliFiles = ["kcap", "kcap-daemon", "libpty_shim.so"];

    public static AppImageInstall? Detect(Func<string, string?> getEnv) =>
        getEnv("APPIMAGE") is { Length: > 0 } image ? new(image) : null;

    /// Copies the bundle's CLI and plugin out of the mount and returns the directory to run the CLI
    /// from. Each file is replaced by rename, so a running daemon keeps its old inode and sees an
    /// update the way it does after `kcap update`. When the copy fails the CLI runs from the mount.
    public string TakeCliOutOfMount(Func<string, string?> getEnv, UserHome home, string appDirectory) {
        var bundleCli = BundleLayout.CliDirectory(appDirectory);
        var dataHome = getEnv("XDG_DATA_HOME") is { } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(home.Path, ".local", "share");
        var root   = Path.Combine(dataHome, "kcap", "desktop");
        var target = Path.Combine(root, "bin");
        var stamp  = Path.Combine(root, "image.stamp");
        var identity = ImageIdentity();

        try {
            if (identity is not null && Staged(stamp, identity, bundleCli, target)) return target;
            Directory.CreateDirectory(target);
            foreach (var name in CliFiles) Stage(Path.Combine(bundleCli, name), Path.Combine(target, name));
            StageTree(BundleLayout.PluginDirectory(appDirectory), BundleLayout.PluginDirectory(root));
            if (identity is not null) File.WriteAllText(stamp, identity);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not copy the bundled CLI to {root}, running it from the AppImage: {ex.Message}");
            return bundleCli;
        }

        return target;
    }

    // The image's size and mtime stand for its contents, so a matching stamp skips hashing the bundle.
    string? ImageIdentity() {
        var image = new FileInfo(Image);
        return image.Exists ? $"{image.Length}:{image.LastWriteTimeUtc.Ticks}" : null;
    }

    static bool Staged(string stamp, string identity, string bundleCli, string target) =>
        File.Exists(stamp) && File.ReadAllText(stamp) == identity
        && CliFiles.Where(name => File.Exists(Path.Combine(bundleCli, name))).All(name => File.Exists(Path.Combine(target, name)));

    static void Stage(string source, string destination) {
        if (!File.Exists(source) || SameContent(source, destination)) return;

        var temp = $"{destination}.{Guid.NewGuid():N}.tmp";
        try {
            File.Copy(source, temp);
            File.Move(temp, destination, overwrite: true);
        } finally {
            File.Delete(temp);
        }
    }

    static void StageTree(string source, string destination) {
        if (!Directory.Exists(source) || SameTree(source, destination)) return;

        var temp = $"{destination}.{Guid.NewGuid():N}.tmp";
        try {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
                var copy = Path.Combine(temp, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(temp, destination);
        } finally {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
    }

    static bool SameTree(string a, string b) {
        if (!Directory.Exists(b)) return false;

        var left  = RelativeFiles(a);
        var right = RelativeFiles(b);

        return left.SetEquals(right) && left.All(f => SameContent(Path.Combine(a, f), Path.Combine(b, f)));
    }

    static HashSet<string> RelativeFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).ToHashSet(StringComparer.Ordinal);

    static bool SameContent(string a, string b) =>
        File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length && Hash(a).AsSpan().SequenceEqual(Hash(b));

    static byte[] Hash(string path) {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}

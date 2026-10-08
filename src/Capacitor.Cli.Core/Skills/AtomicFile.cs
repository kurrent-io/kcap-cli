using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Publishes a file by write-then-rename through a temporary name this call establishes
/// exclusively: the name carries random bytes and the file is created with <c>CreateNew</c>, so
/// nothing can be planted at it and nothing but this call's own temporary is ever removed. A
/// predictable name would be both: a link to follow, and somebody else's file to delete.
///
/// <para>A symlink at the path is written through by default, so a user's dotfile link stays a link
/// and its target takes the content. A caller that refuses links passes <c>followLink: false</c>, so
/// a link planted after its check is replaced rather than written through.</para></summary>
public static class AtomicFile {
    public static void Replace(string path, string contents, UnixFileMode? mode = null, bool followLink = true) =>
        Replace(path, Encoding.UTF8.GetBytes(contents), mode, followLink);

    public static void Replace(string path, byte[] contents, UnixFileMode? mode = null, bool followLink = true) {
        var destination = followLink ? Destination(path) : path;
        var tmp         = $"{destination}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp";
        var created     = false;
        var target      = mode ?? ExistingMode(destination);

        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (target is { } m && !OperatingSystem.IsWindows()) options.UnixCreateMode = m;

            using (var stream = new FileStream(tmp, options)) {
                // Set once the exclusive create has returned, so a name that was somehow already
                // taken is never a name this call cleans up.
                created = true;
                // UnixCreateMode is filtered by the umask; set the exact mode before writing.
                if (target is { } exact && !OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, exact);
                stream.Write(contents);
            }

            File.Move(tmp, destination, overwrite: true);
        } catch {
            if (created) {
                try { File.Delete(tmp); } catch { /* preserve the original exception */ }
            }
            throw;
        }
    }

    // The temporary sits beside the final target: a rename across directories could cross a mount.
    static string Destination(string path) =>
        new FileInfo(path).LinkTarget is null
            ? path
            : File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

    static UnixFileMode? ExistingMode(string path) =>
        !OperatingSystem.IsWindows() && File.Exists(path) ? File.GetUnixFileMode(path) : null;
}

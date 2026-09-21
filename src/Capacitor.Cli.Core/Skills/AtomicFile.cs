using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core.Skills;

/// <summary>Publishes a file by write-then-rename through a temporary name this call establishes
/// exclusively: the name carries random bytes and the file is created with <c>CreateNew</c>, so
/// nothing can be planted at it and nothing but this call's own temporary is ever removed. A
/// predictable name would be both: a link to follow, and somebody else's file to delete.</summary>
public static class AtomicFile {
    public static void Replace(string path, string contents) => Replace(path, Encoding.UTF8.GetBytes(contents));

    public static void Replace(string path, byte[] contents) {
        var tmp     = $"{path}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.tmp";
        var created = false;

        try {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) {
                // Set once the exclusive create has returned, so a name that was somehow already
                // taken is never a name this call cleans up.
                created = true;
                stream.Write(contents);
            }

            File.Move(tmp, path, overwrite: true);
        } catch {
            if (created) {
                try { File.Delete(tmp); } catch { /* preserve the original exception */ }
            }
            throw;
        }
    }
}

namespace Capacitor.Cli.Core.Skills;

/// <summary>Publishes a file by write-then-rename, refusing to write through a symlink planted at
/// the temp name a plain write would otherwise follow.</summary>
public static class AtomicFile {
    public static void Replace(string path, string contents) {
        var tmp = path + ".tmp";
        try {
            // Unlink whatever sits at the temp name — a planted symlink is removed, not followed —
            // then create it exclusively (O_CREAT|O_EXCL) so a link re-planted in the gap is
            // refused rather than written through.
            File.Delete(tmp);
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.Write(contents);
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch { /* preserve the original exception */ }
            throw;
        }
    }
}

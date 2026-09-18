namespace Capacitor.Cli.Core.Skills;

/// <summary>Publishes a file by write-then-rename, refusing to write through a symlink planted at
/// the temp name a plain write would otherwise follow.</summary>
public static class AtomicFile {
    public static void Replace(string path, string contents) {
        var tmp = path + ".tmp";
        try {
            // Delete unlinks rather than follows; the exclusive create (O_CREAT|O_EXCL) then
            // refuses a link re-planted in the gap.
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

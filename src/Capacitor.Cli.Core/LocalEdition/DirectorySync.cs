using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>Persists a directory's entries. Flushing a file does not persist the rename or unlink that
/// published or removed it: after a power loss the directory can still show the old entry.</summary>
static partial class DirectorySync {
    const int ReadOnly = 0;
    const int EPerm    = 1;
    const int EAcces   = 13;

    public static void Flush(string directory) {
        if (OperatingSystem.IsWindows()) return;

        var fd = Open(directory, ReadOnly);
        if (fd < 0) {
            var errno = Marshal.GetLastPInvokeError();
            var message = $"Could not open {directory} to sync it (errno {errno}).";

            throw errno is EPerm or EAcces ? new DirectorySyncDeniedException(message) : new IOException(message);
        }

        try {
            if (Fsync(fd) != 0) throw new IOException($"Could not sync {directory} (errno {Marshal.GetLastPInvokeError()}).");
        } finally {
            // A close that fails after a successful fsync has nothing left to lose.
            _ = Close(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}

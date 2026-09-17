using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Durable, phase-recording record of an in-flight <c>kcap daemon service</c> mutation
/// (install/replace/start). Lives at <c>{daemonsDir}/{id}.service-txn</c>,
/// distinct from <see cref="ServiceTxnLock"/>. A resumer reads the last recorded
/// <see cref="TxnMarker.Phase"/> to decide how far a prior attempt got. Does no locking
/// itself — callers must serialize writes per <c>serviceId</c> via <see cref="ServiceTxnLock"/>.
/// </summary>
public sealed record TxnMarker(
    int Version,
    string Operation,
    string Phase,
    string PreState,
    string SafeState,
    string? PlistFingerprint);

public static partial class ServiceTxnMarker {
    public static bool Exists(DaemonStore store, string daemonName) => File.Exists(store.ServiceTxnPath(daemonName));

    /// <summary>Null on missing OR corrupt — a torn/unreadable marker must never crash a caller.</summary>
    public static TxnMarker? Read(DaemonStore store, string daemonName) {
        var path = store.ServiceTxnPath(daemonName);
        try {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), MarkerJsonContext.Default.TxnMarker);
        } catch {
            return null; // corrupt/unreadable marker — treat as absent
        }
    }

    /// <summary>
    /// Temp file + rename, flushing the file handle to disk both before and after the rename, then a
    /// best-effort fsync of the containing directory. The directory flush is what stops a power loss
    /// preserving the marker's content while losing the rename that published it.
    /// </summary>
    public static void Write(DaemonStore store, string daemonName, TxnMarker marker) {
        store.EnsureDirectory();
        var path = store.ServiceTxnPath(daemonName);
        var tmp  = path + ".tmp";
        var json = JsonSerializer.Serialize(marker, MarkerJsonContext.Default.TxnMarker);

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)) {
            fs.Write(Encoding.UTF8.GetBytes(json));
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);

        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            RandomAccess.FlushToDisk(handle);

        FlushDirectory(Path.GetDirectoryName(path)!);
    }

    public static void Delete(DaemonStore store, string daemonName) {
        var path = store.ServiceTxnPath(daemonName);
        try { File.Delete(path); } catch { /* best-effort */ }
        FlushDirectory(Path.GetDirectoryName(path)!); // durably lose the directory entry, not just the file
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);
    [LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int fsync(int fd);
    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int close(int fd);

    /// <summary>Best-effort fsync of a directory entry: false means the barrier did not fire, which
    /// a caller must tolerate rather than surface.</summary>
    internal static bool FlushDirectory(string dir) {
        if (OperatingSystem.IsWindows()) return false; // no portable directory fsync on Windows

        var fd = -1;
        try {
            fd = open(dir, 0 /* O_RDONLY */);
            if (fd < 0) return false;
            return fsync(fd) == 0;
        } catch {
            return false; // durability hardening only — a failure must never break a marker write
        } finally {
            if (fd >= 0) _ = close(fd);
        }
    }

    public static string Fingerprint(string plistText) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plistText)));

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(TxnMarker))]
    partial class MarkerJsonContext : JsonSerializerContext;
}

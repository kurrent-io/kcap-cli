namespace Capacitor.Cli.Service.Logging;

/// <summary>One file, rolled once to <c>.1</c> at the size cap. A write that fails is dropped: supervision
/// must not stop because its log cannot be written</summary>
public sealed class FileServiceLog(string path, TimeProvider time, long maxBytes = 5 * 1024 * 1024) : IServiceLog {
    readonly Lock _gate = new();

    public void Write(string line) {
        lock (_gate) {
            try {
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes) File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{time.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }
    }
}

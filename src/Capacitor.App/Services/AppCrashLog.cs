using System.Reflection;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>
/// Appends an exception that is about to end the app to <c>app-crash.log</c> under the config root.
/// Launched from Finder or launchd, the app's stderr goes nowhere, and the macOS crash report shows
/// only where Avalonia.Native rethrew a UI-thread exception, never the exception itself.
/// </summary>
public sealed class AppCrashLog(ConfigRoot config, TimeProvider time) {
    public const string FileName = "app-crash.log";

    const long MaxBytes = 256 * 1024;

    static readonly string Version =
        typeof(AppCrashLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    readonly Lock _gate = new();
    Exception?    _last;

    public string Path => config.Path(FileName);

    /// <summary>Never throws. The same exception object reported twice — the UI-thread catch and then
    /// the runtime's unhandled-exception event — is written once.</summary>
    public void Record(string source, Exception ex) {
        lock (_gate) {
            if (ReferenceEquals(ex, _last)) return;

            _last = ex;

            try {
                Directory.CreateDirectory(config.Directory);
                TrimIfLarge();

                // A link here would send the exception text to whatever file it names.
                if (new FileInfo(Path).LinkTarget is not null) return;

                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;

                using var stream = new FileStream(Path, options);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stream.SafeFileHandle, OwnerOnly);
                stream.Write(Encoding.UTF8.GetBytes(FormatEntry(source, ex, time.GetUtcNow())));
            } catch {
                // The process is already failing; a full disk or a permissions error has no better outlet.
            }
        }
    }

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string FormatEntry(string source, Exception ex, DateTimeOffset now) =>
        $"{now.ToUniversalTime():o}  version={Version}  source={source}\n{ex}\n---\n";

    // Best-effort on its own: a log that cannot be trimmed can still take this entry.
    void TrimIfLarge() {
        try {
            var file = new FileInfo(Path);
            if (file.Exists && file.Length > MaxBytes) file.Delete();
        } catch (IOException) {
        } catch (UnauthorizedAccessException) {
        }
    }
}

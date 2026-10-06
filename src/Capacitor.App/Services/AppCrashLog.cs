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

                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using var stream = new FileStream(Path, options);
                stream.Write(Encoding.UTF8.GetBytes(FormatEntry(source, ex, time.GetUtcNow())));
            } catch {
                // The process is already failing; a full disk or a permissions error has no better outlet.
            }
        }
    }

    public static string FormatEntry(string source, Exception ex, DateTimeOffset now) =>
        $"{now.ToUniversalTime():o}  version={Version}  source={source}\n{ex}\n---\n";

    void TrimIfLarge() {
        var file = new FileInfo(Path);
        if (file.Exists && file.Length > MaxBytes) file.Delete();
    }
}

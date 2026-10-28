using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>
/// Install and update milestones, one JSON object per line in <c>app-lifecycle.jsonl</c> under the config root.
/// Successful phases are recorded as well as failures, so a slow update can be reconstructed by phase and a
/// handled failure outlives a process launched from Finder, whose stderr goes nowhere. Values are bounded and
/// the home directory is written as <c>~</c>; callers pass versions, phases, paths and error text, never
/// credentials or payloads. Owner-only, a link is refused, and the file is rotated once past
/// <see cref="MaxBytes"/>. Never throws: a log that cannot be written must not stop an install.
/// </summary>
/// <param name="closed">True once nothing may be written, as after an uninstall removed the config directory,
/// which a write would recreate.</param>
public sealed class LifecycleLog(ConfigRoot config, TimeProvider time, string home, Func<bool>? closed = null) {
    public const string FileName = "app-lifecycle.jsonl";
    internal const long MaxBytes = 1024 * 1024;
    const int MaxValue = 500;
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    static readonly Lock Gate = new();
    static readonly string Version =
        typeof(LifecycleLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    public string Path => config.Path(FileName);

    public void Write(string evt, params (string Key, string? Value)[] fields) {
        if (closed?.Invoke() == true) return;
        try {
            var entry = new JsonObject {
                ["ts"] = time.GetUtcNow().ToString("o"),
                ["pid"] = Environment.ProcessId,
                ["version"] = Version,
                ["event"] = evt,
            };
            foreach (var (key, value) in fields)
                if (value is not null) entry[key] = Bound(value);
            var line = Encoding.UTF8.GetBytes(entry.ToJsonString() + "\n");

            lock (Gate) {
                Directory.CreateDirectory(config.Directory);
                if (new FileInfo(Path).LinkTarget is not null) return;
                Rotate();
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
                using var stream = new FileStream(Path, options);
                stream.Write(line);
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not write the lifecycle log: {ex.Message}");
        }
    }

    string Bound(string value) {
        var text = home.Length > 1 ? value.Replace(home, "~", StringComparison.Ordinal) : value;
        return text.Length <= MaxValue ? text : text[..MaxValue] + "…";
    }

    void Rotate() {
        var file = new FileInfo(Path);
        if (!file.Exists || file.Length < MaxBytes) return;
        var previous = Path + ".1";
        if (new FileInfo(previous).LinkTarget is not null) File.Delete(previous);
        File.Move(Path, previous, overwrite: true);
    }
}

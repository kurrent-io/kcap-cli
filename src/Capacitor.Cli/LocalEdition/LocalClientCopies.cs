using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// The kcap that <c>PATH</c> runs and the desktop app's bundled one, each asked for its version. The running kcap is
/// left out, and a copy reached by both is counted once, as the app's: updating the app is what moves it.
/// </summary>
sealed class LocalClientCopies(IProcessRunner runner, string? searchPath, string? self, IReadOnlyList<string> appClis) : ILocalClientCopies {
    static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    const int OutputLimit = 4096;

    IReadOnlyList<ClientCopy>? _found;

    public static LocalClientCopies ForCurrentProcess(UserHome home, TimeProvider time) {
        string[] apps = OperatingSystem.IsMacOS()
            ? [.. AppClis("/Applications"), .. AppClis(Path.Combine(home.Path, "Applications"))]
            : [];

        return new(new ProcessRunner(time), Environment.GetEnvironmentVariable("PATH"), Environment.ProcessPath, apps);
    }

    internal static string[] AppClis(string applications) => [
        Path.Combine(applications, "kcap.app", "Contents", "MacOS", "kcap"),
        Path.Combine(applications, "Kurrent Capacitor.app", "Contents", "MacOS", "kcap")
    ];

    public IReadOnlyList<ClientCopy> Find() => _found ??= Locate().Select(c => c with { Version = VersionOf(c.Path) }).ToList();

    IEnumerable<ClientCopy> Locate() {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (self is not null) seen.Add(CanonicalPath.Resolve(self));

        foreach (var app in appClis.Where(File.Exists)) {
            if (seen.Add(CanonicalPath.Resolve(app))) yield return new(ClientCopyKind.DesktopApp, app, null);
        }

        if (BinaryProbe.Searching(searchPath).Resolve("kcap") is { } onPath && seen.Add(CanonicalPath.Resolve(onPath)))
            yield return new(ClientCopyKind.OnPath, onPath, null);
    }

    /// <summary>Without <c>--no-update-check</c> the copy would run its npm update check on the way out.</summary>
    string? VersionOf(string path) {
        ProcessResult result;

        try {
            result = runner.RunBoundedAsync(path, ["--version", "--no-update-check"], new(Timeout: VersionTimeout), OutputLimit, CancellationToken.None)
                .GetAwaiter().GetResult();
        } catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) {
            return null;
        }

        return result is { ExitCode: 0, TimedOut: false, Oversized: false } ? Parse(result.Stdout) : null;
    }

    /// <summary>Exactly one line, <c>kcap &lt;version&gt;</c>.</summary>
    static string? Parse(string stdout) {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines is not [var line] || !line.StartsWith("kcap ", StringComparison.Ordinal)) return null;

        var version = line["kcap ".Length..].Trim();

        return version is "" or "unknown" ? null : version;
    }
}

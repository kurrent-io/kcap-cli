using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The kcap that <c>PATH</c> runs and the desktop app's bundled one, each asked for its version. The running kcap is
/// left out, and a copy reached by both is counted once, as the app's: updating the app is what moves it.
/// </summary>
public sealed class LocalClientCopies(IProcessRunner runner, string? searchPath, string? self, IReadOnlyList<string> appClis) : ILocalClientCopies {
    static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    const int OutputLimit = 4096;

    IReadOnlyList<ClientCopy>? _found;

    public static LocalClientCopies ForCurrentProcess(UserHome home, TimeProvider time) =>
        new(new ProcessRunner(time), Environment.GetEnvironmentVariable("PATH"), Environment.ProcessPath, AppClis(home));

    /// <summary>Where a desktop app's bundled kcap can be.</summary>
    public static IReadOnlyList<string> AppClis(UserHome home) =>
        OperatingSystem.IsMacOS() ? [AppCli("/Applications"), AppCli(Path.Combine(home.Path, "Applications"))] : [];

    public static string AppCli(string applications) => Path.Combine(applications, "kcap.app", "Contents", "MacOS", "kcap");

    public IReadOnlyList<ClientCopy> Find() => _found ??= Locate().Select(c => c with { Version = VersionOf(runner, c.Path) }).ToList();

    IEnumerable<ClientCopy> Locate() {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (self is not null) seen.Add(CanonicalPath.Resolve(self));

        foreach (var app in appClis.Where(File.Exists)) {
            if (seen.Add(CanonicalPath.Resolve(app))) yield return new(ClientCopyKind.DesktopApp, app, null);
        }

        if (BinaryProbe.Searching(searchPath).Resolve("kcap") is { } onPath && seen.Add(CanonicalPath.Resolve(onPath)))
            yield return new(ClientCopyKind.OnPath, onPath, null);
    }

    /// <summary>The version the kcap at <paramref name="path"/> reports, or null when it reports none. Without
    /// <c>--no-update-check</c> the copy would run its npm update check on the way out.</summary>
    public static string? VersionOf(IProcessRunner runner, string path) {
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

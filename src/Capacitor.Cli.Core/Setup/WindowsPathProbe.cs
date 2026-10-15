namespace Capacitor.Cli.Core.Setup;

/// The Windows answer to <see cref="ILoginShellProbe"/>. A new terminal's PATH is the machine PATH
/// followed by the user PATH as the registry holds them now, not this process's copy: a PATH edit
/// made after the app started reaches every new terminal but never this process.
public sealed class WindowsPathProbe(Func<string?> machinePath, Func<string?> userPath, Func<string, bool> fileExists, string? pathExt)
    : ILoginShellProbe {
    const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    public WindowsPathProbe()
        : this(
            () => Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
            () => Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
            File.Exists,
            Environment.GetEnvironmentVariable("PATHEXT")) { }

    public Task<string?> TerminalPathAsync(CancellationToken ct) => Task.FromResult(TerminalPath());

    public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) =>
        Task.FromResult<bool?>(TerminalPath() is { } path ? Find(path) is not null : null);

    public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) =>
        Task.FromResult(TerminalPath() is { } path ? Find(path) : null);

    string? TerminalPath() {
        var parts = new[] { machinePath(), userPath() }.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        return parts.Length == 0 ? null : string.Join(';', parts);
    }

    /// Resolves `kcap` the way cmd.exe does: each PATH directory in order, each PATHEXT extension in
    /// order — so an npm `kcap.cmd` counts as much as a `kcap.exe`.
    string? Find(string path) {
        var extensions = (string.IsNullOrWhiteSpace(pathExt) ? DefaultPathExt : pathExt)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var unquoted = dir.Trim('"');
            if (!Path.IsPathFullyQualified(unquoted)) continue;
            foreach (var ext in extensions) {
                var candidate = Path.Combine(unquoted, "kcap" + ext.ToLowerInvariant());
                if (fileExists(candidate)) return candidate;
            }
        }

        return null;
    }
}

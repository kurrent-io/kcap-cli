namespace Capacitor.Cli.Commands;

/// <summary>The detached child's contract with setup: where to write, and which visibility to stamp.
/// Read only when the log variable is present; a plain <c>kcap import</c> never sees either.</summary>
internal sealed record DetachedImportLog(string LogPath, string? DefaultVisibility) {
    public const string EnvVar           = "KCAP_IMPORT_DETACHED_LOG";
    public const string VisibilityEnvVar = "KCAP_IMPORT_DEFAULT_VISIBILITY";

    public static DetachedImportLog? FromEnvironment(Func<string, string?> getEnv) {
        var log = getEnv(EnvVar);
        if (string.IsNullOrWhiteSpace(log)) return null;

        var visibility = getEnv(VisibilityEnvVar);
        return new DetachedImportLog(log, string.IsNullOrWhiteSpace(visibility) ? null : visibility);
    }

    /// <summary>Setup created the file; the child only ever appends to it.</summary>
    public StreamWriter Open() {
        var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        stream.Seek(0, SeekOrigin.End);
        return new StreamWriter(stream) { AutoFlush = true };
    }
}

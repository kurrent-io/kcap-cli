namespace Capacitor.Cli.Core.Pi;

/// <summary>
/// Filesystem layout for Pi (badlogic/pi-mono, the <c>pi</c> CLI). Pi keeps its
/// agent state under <c>~/.pi/agent</c>: sessions as tree-structured JSONL in
/// <c>sessions/</c> (organized by working directory), and auto-discovered
/// TypeScript extensions in <c>extensions/</c>.
///
/// Pi has <b>no shell hooks</b>, so kcap's live integration ships as an
/// extension file (<see cref="KcapExtension"/>, written by <c>kcap plugin --pi</c>)
/// rather than a hooks.json the way Copilot/Cursor do.
/// </summary>
public static class PiPaths {
    public static string Root(string? home = null) {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pi");
    }

    public static string AgentDir(string? home = null) => Path.Combine(Root(home), "agent");

    /// <summary>
    /// Session JSONL root — one <c>*.jsonl</c> per session, possibly nested in
    /// per-cwd subdirectories. Each file's first line is the <c>session</c>
    /// header (full uuid <c>id</c>, <c>cwd</c>, ISO <c>timestamp</c>); discovery
    /// walks this tree recursively. Pi's <c>--session-dir</c> can relocate it,
    /// so callers may override.
    /// </summary>
    public static string SessionsDir(string? home = null) => Path.Combine(AgentDir(home), "sessions");

    /// <summary>Auto-discovered extensions dir; kcap installs <see cref="KcapExtension"/> here.</summary>
    public static string ExtensionsDir(string? home = null) => Path.Combine(AgentDir(home), "extensions");

    public static string KcapExtension(string? home = null) => Path.Combine(ExtensionsDir(home), "kcap.ts");

    /// <summary>Marker recording the installed extension version (sibling of kcap.ts).</summary>
    public static string KcapExtensionMarker(string? home = null) => Path.Combine(ExtensionsDir(home), ".kcap-extension-version");

    /// <summary>
    /// Detection by <c>~/.pi/agent</c> presence — Pi creates it on first run.
    /// The binary name <c>pi</c> is too generic for a PATH probe to be the only
    /// signal, so callers that also want the PATH probe OR this with
    /// <c>AgentDetector.IsInstalled("pi")</c>.
    /// </summary>
    public static bool IsInstalled(string? home = null) => Directory.Exists(AgentDir(home));
}

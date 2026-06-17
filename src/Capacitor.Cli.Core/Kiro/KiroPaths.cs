namespace Capacitor.Cli.Core.Kiro;

/// <summary>
/// Filesystem layout for AWS Kiro CLI (the rebranded Amazon Q Developer CLI,
/// <c>kiro-cli</c>). Kiro keeps two distinct trees:
/// <list type="bullet">
///   <item><b>Content DB</b> — a single SQLite <c>data.sqlite3</c> under the OS
///     data dir (macOS <c>~/Library/Application Support/kiro-cli</c>, Linux
///     <c>~/.local/share/kiro-cli</c>, Windows <c>%LOCALAPPDATA%\kiro-cli</c>).
///     The conversation lives in the <c>conversations_v2</c> table as a single
///     JSON <c>ConversationState</c> blob — NOT an append-only JSONL log. The
///     watcher / import path reads it through <see cref="KiroTranscriptReader"/>.</item>
///   <item><b>Config tree</b> — <c>~/.kiro</c> (agent JSON under <c>agents/</c>,
///     MCP config under <c>settings/</c>). kcap installs its lifecycle hooks
///     into <see cref="KcapAgentJson"/>.</item>
/// </list>
///
/// Overrides (Kiro's schema is officially undocumented — kirodotdev/Kiro #5094 —
/// so treat these as version-fragile): <c>KIRO_CLI_DB_FILE</c> pins the exact DB
/// file (the precise, test-friendly knob); <c>KIRO_HOME</c> relocates BOTH trees
/// to a single root (<c>{KIRO_HOME}</c> as the config root and
/// <c>{KIRO_HOME}/data.sqlite3</c> as the DB) — the most useful interpretation
/// for sandboxing an alternate Kiro install.
/// </summary>
public static class KiroPaths {
    /// <summary>Kiro's config root (<c>~/.kiro</c>; agents + settings). Relocated by <c>KIRO_HOME</c>.</summary>
    public static string ConfigRoot(string? home = null) {
        var kiroHome = Environment.GetEnvironmentVariable("KIRO_HOME");
        if (!string.IsNullOrEmpty(kiroHome)) return kiroHome;

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".kiro");
    }

    /// <summary>OS data dir that holds <c>kiro-cli/data.sqlite3</c>.</summary>
    static string DataRoot(string? home = null) {
        home ??= PathHelpers.HomeDirectory;

        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "kiro-cli");

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kiro-cli");

        // Linux / other: honor XDG_DATA_HOME, else ~/.local/share.
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg;
        return Path.Combine(dataHome, "kiro-cli");
    }

    /// <summary>
    /// Absolute path to the SQLite conversation DB. <c>KIRO_CLI_DB_FILE</c> wins
    /// (exact path); else <c>{KIRO_HOME}/data.sqlite3</c> when KIRO_HOME is set;
    /// else the OS data-dir default.
    /// </summary>
    public static string DbPath(string? home = null) {
        var dbFile = Environment.GetEnvironmentVariable("KIRO_CLI_DB_FILE");
        if (!string.IsNullOrEmpty(dbFile)) return dbFile;

        var kiroHome = Environment.GetEnvironmentVariable("KIRO_HOME");
        if (!string.IsNullOrEmpty(kiroHome)) return Path.Combine(kiroHome, "data.sqlite3");

        return Path.Combine(DataRoot(home), "data.sqlite3");
    }

    /// <summary>User-level agent config dir (<c>~/.kiro/agents</c>).</summary>
    public static string AgentsDir(string? home = null) => Path.Combine(ConfigRoot(home), "agents");

    /// <summary>
    /// kcap's owned agent-hooks file. Mirrors the Copilot model: kcap owns its
    /// own file rather than merging into a user agent, so removal is a clean
    /// delete. Kiro reads every <c>agents/*.json</c>, so the lifecycle hooks
    /// here apply to Kiro sessions.
    /// </summary>
    public static string KcapAgentJson(string? home = null) => Path.Combine(AgentsDir(home), "kcap.json");

    /// <summary>
    /// ACP-mode session JSONL dir (<c>~/.kiro/sessions/cli</c>). Tail-friendly,
    /// but only present for ACP sessions — a future enhancement, not the v1
    /// content source (which is the SQLite DB above).
    /// </summary>
    public static string AcpSessionsDir(string? home = null) => Path.Combine(ConfigRoot(home), "sessions", "cli");

    /// <summary>
    /// Detection: the config tree exists OR the conversation DB exists. The
    /// binary name (<c>kiro</c> / <c>kiro-cli</c>) is also probed by callers via
    /// <c>AgentDetector.IsInstalled</c>; OR the two for the widest coverage.
    /// </summary>
    public static bool IsInstalled(string? home = null) =>
        Directory.Exists(ConfigRoot(home)) || File.Exists(DbPath(home));

    /// <summary>
    /// kcap-owned directory holding the SQLite-flattened transcript files the
    /// watcher tails. Kiro has no on-disk JSONL transcript, so the watcher
    /// materializes one here (<see cref="KiroTranscriptReader"/>) and the shared
    /// file-tail drain loop consumes it like every other vendor.
    /// </summary>
    public static string MaterializedTranscriptDir() => PathHelpers.ConfigPath("kiro-transcripts");

    /// <summary>Materialized transcript path for a dashless session id.</summary>
    public static string MaterializedTranscript(string dashlessSessionId) =>
        Path.Combine(MaterializedTranscriptDir(), $"{dashlessSessionId}.jsonl");
}

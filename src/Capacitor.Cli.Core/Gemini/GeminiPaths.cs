namespace Capacitor.Cli.Core.Gemini;

/// <summary>
/// Filesystem layout for Google Gemini CLI state. Everything lives under a
/// single root: <c>$GEMINI_HOME</c> when set, otherwise <c>~/.gemini</c> on
/// every OS. Unlike Copilot's dedicated <c>hooks/kcap.json</c>, Gemini's hooks
/// live in the SHARED <c>settings.json</c> under a <c>hooks</c> key, so the
/// installer must MERGE (see <see cref="GeminiHooksParser"/>).
/// </summary>
public static class GeminiPaths {
    public static string Root(string? home = null, string? geminiHome = null) {
        geminiHome ??= Environment.GetEnvironmentVariable("GEMINI_HOME");
        if (!string.IsNullOrEmpty(geminiHome)) return geminiHome;

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".gemini");
    }

    /// <summary>
    /// Detection by root-dir presence — Gemini CLI creates <c>~/.gemini</c> on
    /// first run; the binary name <c>gemini</c> is too generic for a PATH probe
    /// to be the only signal. Callers that also want the PATH probe OR this
    /// with <c>AgentDetector.IsInstalled("gemini")</c>.
    /// </summary>
    public static bool IsInstalled(string? home = null, string? geminiHome = null)
        => Directory.Exists(Root(home, geminiHome));

    /// <summary>
    /// Shared settings file (<c>~/.gemini/settings.json</c>) — holds user config
    /// plus the <c>hooks</c> block kcap merges into. NEVER overwrite wholesale.
    /// </summary>
    public static string SettingsJson(string? home = null, string? geminiHome = null)
        => Path.Combine(Root(home, geminiHome), "settings.json");

    /// <summary>
    /// Per-project temporary state root: <c>~/.gemini/tmp/&lt;project&gt;/</c>.
    /// Chat recordings live under <c>chats/</c> within each project dir.
    /// </summary>
    public static string TmpDir(string? home = null, string? geminiHome = null)
        => Path.Combine(Root(home, geminiHome), "tmp");

    /// <summary>Chat-recording directory for a project tmp dir: <c>&lt;tmp&gt;/&lt;project&gt;/chats</c>.</summary>
    public static string ChatsDir(string projectTmpDir)
        => Path.Combine(projectTmpDir, "chats");

    /// <summary>
    /// Nested subagent-recording directory for a parent session:
    /// <c>&lt;chats&gt;/&lt;parentSessionId&gt;/</c>. Gemini records each subagent's transcript
    /// here as <c>&lt;subId&gt;.jsonl</c> (subId = a fresh dashed UUID — the executor's agent
    /// id; nested subagents at any depth stay flat under the top-level session).
    /// <paramref name="parentSessionId"/> is the DASHED form from the parent transcript's
    /// header, matching the on-disk directory name. AI-900.
    /// </summary>
    public static string SubagentDir(string chatsDir, string parentSessionId)
        => Path.Combine(chatsDir, parentSessionId);
}

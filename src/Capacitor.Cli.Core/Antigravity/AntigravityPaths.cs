using Capacitor.Cli.Core.Gemini;

namespace Capacitor.Cli.Core.Antigravity;

/// <summary>
/// Filesystem layout for Google Antigravity (AI-1157/AI-1158). Antigravity is a
/// GUI IDE (VS Code fork, Windsurf/Codeium lineage) whose agent state lives under
/// the SHARED <c>~/.gemini</c> home in an <c>antigravity</c> subdir (so paths reuse
/// <see cref="GeminiPaths.Root"/> and honor <c>GEMINI_CLI_HOME</c>). Each conversation
/// has a per-conversation JSONL transcript under <c>brain/&lt;id&gt;/…/logs/</c> and a
/// SQLite <c>conversations/&lt;id&gt;.db</c> (token/model in its protobuf <c>gen_metadata</c>).
///
/// kcap ships as an Antigravity <b>plugin</b>: a directory under the GUI config root
/// (<c>~/.gemini/config/plugins/kcap/</c>) holding a required <c>plugin.json</c> marker
/// (without it the GUI never loads the dir) plus a <c>hooks.json</c> that registers the
/// kcap control hooks. Per-workspace installs live under <c>&lt;root&gt;/.agents/plugins/kcap/</c>.
/// ⚠️ The <c>~/.gemini/antigravity-cli/</c> dir is the <c>agy</c> CLI's config root — the
/// GUI does NOT read it, so installing hooks there (as #256 originally did) is invisible
/// to the running IDE (AI-1158 GUI re-test).
///
/// ⚠️ <c>~/.gemini</c> is shared with the Gemini CLI — <see cref="GeminiPaths.IsInstalled"/>
/// must require a Gemini-specific marker so an Antigravity-only home doesn't read as
/// a Gemini install.
/// </summary>
public static class AntigravityPaths {
    /// <summary>Antigravity data root: <c>&lt;gemini-root&gt;/antigravity</c>.</summary>
    public static string Root(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GeminiPaths.Root(home, geminiCliHome), "antigravity");

    /// <summary>GUI config root the IDE reads plugins from: <c>&lt;gemini-root&gt;/config</c>.</summary>
    public static string GuiConfigRoot(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GeminiPaths.Root(home, geminiCliHome), "config");

    /// <summary>The kcap capture plugin dir the GUI loads: <c>&lt;gui-config&gt;/plugins/kcap</c>.</summary>
    public static string PluginDir(string? home = null, string? geminiCliHome = null)
        => Path.Combine(GuiConfigRoot(home, geminiCliHome), "plugins", AntigravityHooks.BlockName);

    /// <summary>Per-conversation "brain" dir: <c>&lt;root&gt;/brain/&lt;id&gt;</c>.</summary>
    public static string BrainDir(string conversationId, string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "brain", conversationId);

    /// <summary>Full JSONL transcript: <c>&lt;brain&gt;/.system_generated/logs/transcript_full.jsonl</c>.</summary>
    public static string TranscriptFullPath(string conversationId, string? home = null, string? geminiCliHome = null)
        => Path.Combine(BrainDir(conversationId, home, geminiCliHome), ".system_generated", "logs", "transcript_full.jsonl");

    /// <summary>Inter-agent messages dir (child→parent linkage): <c>&lt;brain&gt;/.system_generated/messages</c>.</summary>
    public static string MessagesDir(string conversationId, string? home = null, string? geminiCliHome = null)
        => Path.Combine(BrainDir(conversationId, home, geminiCliHome), ".system_generated", "messages");

    /// <summary>Per-conversation SQLite db (protobuf gen_metadata → tokens/model): <c>&lt;root&gt;/conversations/&lt;id&gt;.db</c>.</summary>
    public static string ConversationDb(string conversationId, string? home = null, string? geminiCliHome = null)
        => Path.Combine(Root(home, geminiCliHome), "conversations", $"{conversationId}.db");

    /// <summary>
    /// The gen_metadata db that is a sibling of a conversation's <c>transcript_full.jsonl</c>.
    /// Derives the real (dashed) conversation id from the transcript path — the brain-dir name
    /// — so callers holding only the transcript path (e.g. the watcher, which sees a canonical
    /// dashless session id) still resolve the correct <c>conversations/&lt;id&gt;.db</c>. Returns
    /// null when the path doesn't match the expected
    /// <c>&lt;root&gt;/brain/&lt;id&gt;/.system_generated/logs/transcript_full.jsonl</c> shape.
    /// </summary>
    public static string? ConversationDbFromTranscript(string transcriptPath) {
        // Require the EXACT shape …/brain/<id>/.system_generated/logs/transcript_full.jsonl —
        // validate each segment so an unexpected path fails open (returns null) instead of
        // being mapped to a guessed <root>/conversations/<derived>.db (AI-1157 review).
        if (!string.Equals(Path.GetFileName(transcriptPath), "transcript_full.jsonl", StringComparison.Ordinal))
            return null;

        var logsDir = Path.GetDirectoryName(transcriptPath);                 // …/logs
        var sysGen  = Path.GetDirectoryName(logsDir);                        // …/.system_generated
        var convDir = Path.GetDirectoryName(sysGen);                         // …/<id>
        var brain   = Path.GetDirectoryName(convDir);                        // …/brain
        var root    = Path.GetDirectoryName(brain);                          // …/<root>
        if (convDir is null || brain is null || root is null) return null;

        if (!string.Equals(Path.GetFileName(logsDir), "logs",              StringComparison.Ordinal)) return null;
        if (!string.Equals(Path.GetFileName(sysGen),  ".system_generated", StringComparison.Ordinal)) return null;
        if (!string.Equals(Path.GetFileName(brain),   "brain",             StringComparison.Ordinal)) return null;

        var convId = Path.GetFileName(convDir);
        if (string.IsNullOrEmpty(convId)) return null;

        return Path.Combine(root, "conversations", $"{convId}.db");
    }

    /// <summary>Global hooks config the kcap plugin installs into: <c>&lt;plugin-dir&gt;/hooks.json</c>.</summary>
    public static string GlobalHooksJson(string? home = null, string? geminiCliHome = null)
        => Path.Combine(PluginDir(home, geminiCliHome), "hooks.json");

    /// <summary>Plugin manifest marker the GUI requires: <c>&lt;plugin-dir&gt;/plugin.json</c>.</summary>
    public static string GlobalPluginManifest(string? home = null, string? geminiCliHome = null)
        => Path.Combine(PluginDir(home, geminiCliHome), "plugin.json");

    /// <summary>Per-workspace plugin dir (opt-in): <c>&lt;workspaceRoot&gt;/.agents/plugins/kcap</c>.</summary>
    public static string WorkspacePluginDir(string workspaceRoot)
        => Path.Combine(workspaceRoot, ".agents", "plugins", AntigravityHooks.BlockName);

    /// <summary>Per-workspace hooks config (opt-in): <c>&lt;workspaceRoot&gt;/.agents/plugins/kcap/hooks.json</c>.</summary>
    public static string WorkspaceHooksJson(string workspaceRoot)
        => Path.Combine(WorkspacePluginDir(workspaceRoot), "hooks.json");

    /// <summary>
    /// Detection by data-root presence — Antigravity creates <c>~/.gemini/antigravity</c>
    /// on first run. (The app bundle can also be probed by callers.)
    /// </summary>
    public static bool IsInstalled(string? home = null, string? geminiCliHome = null)
        => Directory.Exists(Root(home, geminiCliHome));
}

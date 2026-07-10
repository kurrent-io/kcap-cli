using Capacitor.Cli.Core.Antigravity;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravityPaths"/> (AI-1158). Antigravity data lives
/// under the shared <c>~/.gemini</c> home in an <c>antigravity</c> subdir; paths are
/// asserted against the captured on-disk layout (AI-1150 spike). Parallel-safe: the
/// <c>geminiCliHome</c> override is non-null, so no env var is read.
/// </summary>
public class AntigravityPathsTests {
    const string P = "/fake/parent";
    static string GeminiRoot => Path.Combine(P, ".gemini");

    [Test]
    public async Task Root_is_antigravity_under_gemini_home() {
        await Assert.That(AntigravityPaths.Root(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity"));
    }

    // AI-1158 GUI re-test: the plugin must land under the GUI config root
    // (~/.gemini/config/plugins/kcap/), NOT the agy CLI dir (~/.gemini/antigravity-cli/),
    // which the running IDE never reads.
    [Test]
    public async Task PluginDir_and_GlobalHooksJson_and_manifest_are_under_gui_config() {
        var pluginDir = Path.Combine(GeminiRoot, "config", "plugins", "kcap");
        await Assert.That(AntigravityPaths.GuiConfigRoot(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "config"));
        await Assert.That(AntigravityPaths.PluginDir(home: "/h", geminiCliHome: P))
            .IsEqualTo(pluginDir);
        await Assert.That(AntigravityPaths.GlobalHooksJson(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(pluginDir, "hooks.json"));
        await Assert.That(AntigravityPaths.GlobalPluginManifest(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(pluginDir, "plugin.json"));
    }

    // MCP config is Antigravity's OWN file under the GUI config root (NOT the Gemini CLI's
    // settings.json); the steering file + skills dir are SHARED with Gemini under ~/.gemini.
    [Test]
    public async Task McpConfigJson_is_under_gui_config() {
        await Assert.That(AntigravityPaths.McpConfigJson(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "config", "mcp_config.json"));
    }

    [Test]
    public async Task InstructionsMd_is_the_shared_gemini_md() {
        await Assert.That(AntigravityPaths.InstructionsMd(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "GEMINI.md"));
    }

    [Test]
    public async Task SkillsDir_is_gemini_skills_not_agents_skills() {
        await Assert.That(AntigravityPaths.SkillsDir(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "skills"));
    }

    [Test]
    public async Task TranscriptFullPath_matches_captured_layout() {
        await Assert.That(AntigravityPaths.TranscriptFullPath("conv1", home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity", "brain", "conv1", ".system_generated", "logs", "transcript_full.jsonl"));
    }

    [Test]
    public async Task MessagesDir_and_ConversationDb() {
        await Assert.That(AntigravityPaths.MessagesDir("conv1", home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity", "brain", "conv1", ".system_generated", "messages"));
        await Assert.That(AntigravityPaths.ConversationDb("conv1", home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity", "conversations", "conv1.db"));
    }

    [Test]
    public async Task WorkspaceHooksJson_is_dot_agents_plugin_dir() {
        await Assert.That(AntigravityPaths.WorkspacePluginDir("/repo"))
            .IsEqualTo(Path.Combine("/repo", ".agents", "plugins", "kcap"));
        await Assert.That(AntigravityPaths.WorkspaceHooksJson("/repo"))
            .IsEqualTo(Path.Combine("/repo", ".agents", "plugins", "kcap", "hooks.json"));
    }

    [Test]
    public async Task IsInstalled_true_only_when_data_root_exists() {
        var home = Path.Combine(Path.GetTempPath(), "kcap-ag-" + Guid.NewGuid().ToString("N"));
        try {
            // geminiCliHome: "" forces home-based resolution (no env read).
            await Assert.That(AntigravityPaths.IsInstalled(home: home, geminiCliHome: "")).IsFalse();

            Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity"));
            await Assert.That(AntigravityPaths.IsInstalled(home: home, geminiCliHome: "")).IsTrue();
        } finally {
            if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
        }
    }

    // AI-1158 review (C2): the watcher sees a dashless session id but must resolve the
    // real (dashed) conversation's sibling gen_metadata db from the transcript path.
    [Test]
    public async Task ConversationDbFromTranscript_resolves_the_sibling_db() {
        var transcript = AntigravityPaths.TranscriptFullPath("abc-123-def", home: "/h", geminiCliHome: P);
        // GetFullPath normalizes separators so the assertion isn't brittle across OSes:
        // ConversationDbFromTranscript walks up with GetDirectoryName (which canonicalizes
        // separators on Windows) while ConversationDb builds via Path.Combine — same file,
        // possibly different separator style in the raw string.
        await Assert.That(Path.GetFullPath(AntigravityPaths.ConversationDbFromTranscript(transcript)!))
            .IsEqualTo(Path.GetFullPath(AntigravityPaths.ConversationDb("abc-123-def", home: "/h", geminiCliHome: P)));
    }

    [Test]
    [Arguments("foo.jsonl")]                                                              // wrong filename, shallow
    [Arguments("/a/b/c/d/e/transcript_full.jsonl")]                                       // right file, wrong segments
    [Arguments("/root/brain/id/.system_generated/logs/other.jsonl")]                      // wrong filename
    [Arguments("/root/brain/id/.system_generated/notlogs/transcript_full.jsonl")]         // wrong "logs" segment
    [Arguments("/root/brain/id/wrong/logs/transcript_full.jsonl")]                        // wrong ".system_generated"
    [Arguments("/root/notbrain/id/.system_generated/logs/transcript_full.jsonl")]         // wrong "brain"
    public async Task ConversationDbFromTranscript_returns_null_for_an_unexpected_path(string path) {
        await Assert.That(AntigravityPaths.ConversationDbFromTranscript(path)).IsNull();
    }
}

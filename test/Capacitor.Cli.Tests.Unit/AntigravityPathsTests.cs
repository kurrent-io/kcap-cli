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

    [Test]
    public async Task CliRoot_and_GlobalHooksJson() {
        await Assert.That(AntigravityPaths.CliRoot(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity-cli"));
        await Assert.That(AntigravityPaths.GlobalHooksJson(home: "/h", geminiCliHome: P))
            .IsEqualTo(Path.Combine(GeminiRoot, "antigravity-cli", "hooks.json"));
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
    public async Task WorkspaceHooksJson_is_dot_agents() {
        await Assert.That(AntigravityPaths.WorkspaceHooksJson("/repo"))
            .IsEqualTo(Path.Combine("/repo", ".agents", "hooks.json"));
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
}

using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudeLauncherWriteJsonAtomicTests {
    // Regression: with CLAUDE_CONFIG_DIR set, the user-global .claude.json lives at
    // $CLAUDE_CONFIG_DIR/.claude.json, and that directory may not exist yet (fresh /
    // freshly-relocated config). WriteJsonAtomic must create the parent dir, or the
    // trust write throws DirectoryNotFoundException and agents re-prompt for trust.
    [Test]
    public async Task WriteJsonAtomic_creates_missing_parent_directory() {
        var root = Directory.CreateTempSubdirectory("kcap-wja-test-");
        try {
            var path = Path.Combine(root.FullName, "fresh-config-dir", ".claude.json");
            var node = new JsonObject { ["projects"] = new JsonObject() };

            ClaudeLauncher.WriteJsonAtomic(path, node);

            await Assert.That(File.Exists(path)).IsTrue();
        } finally {
            root.Delete(recursive: true);
        }
    }
}

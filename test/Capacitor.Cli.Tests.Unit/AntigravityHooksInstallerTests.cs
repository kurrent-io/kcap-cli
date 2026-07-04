using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Antigravity;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravityHooksInstaller"/> / <see cref="AntigravityHooks"/>
/// (AI-1158): the kcap block is installed with the two shape variants, user blocks are
/// preserved, remove strips only kcap's block, and malformed JSON is backed up (never
/// silently clobbered).
/// </summary>
public class AntigravityHooksInstallerTests {
    static string TempDir() {
        var d = Path.Combine(Path.GetTempPath(), "kcap-agh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Test]
    public async Task Install_writes_kcap_block_with_both_entry_shapes() {
        var dir  = TempDir();
        var path = Path.Combine(dir, "hooks.json");
        try {
            AntigravityHooksInstaller.Install(path);

            var root  = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var block = (JsonObject)root[AntigravityHooks.BlockName]!;

            // Lifecycle event: DIRECT handler list, distinct per-event command.
            var stop = (JsonArray)block["Stop"]!;
            await Assert.That((string?)stop[0]!["command"]).IsEqualTo("kcap hook --antigravity Stop");
            await Assert.That(stop[0]!["matcher"]).IsNull();

            // Tool event: matcher + nested hooks[].
            var pre = (JsonArray)block["PreToolUse"]!;
            await Assert.That((string?)pre[0]!["matcher"]).IsEqualTo("*");
            await Assert.That((string?)pre[0]!["hooks"]![0]!["command"]).IsEqualTo("kcap hook --antigravity PreToolUse");

            // All five events present.
            foreach (var e in AntigravityHooks.LifecycleEvents.Concat(AntigravityHooks.ToolEvents))
                await Assert.That(block.ContainsKey(e)).IsTrue();

            await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsTrue();
        } finally { Directory.Delete(dir, recursive: true); }
    }

    // AI-1158 GUI re-test: the GUI only loads a plugin dir that contains a plugin.json
    // manifest — without it, hooks.json is never read. Install must write it; Remove must
    // clean it up.
    [Test]
    public async Task Install_writes_plugin_manifest_marker_and_Remove_deletes_it() {
        var dir      = TempDir();
        var path     = Path.Combine(dir, "hooks.json");
        var manifest = Path.Combine(dir, AntigravityHooksInstaller.PluginManifestFileName);
        try {
            AntigravityHooksInstaller.Install(path);

            await Assert.That(File.Exists(manifest)).IsTrue();
            var m = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
            await Assert.That((string?)m["name"]).IsEqualTo(AntigravityHooks.BlockName);
            await Assert.That(m.ContainsKey("version")).IsTrue();

            AntigravityHooksInstaller.Remove(path);
            await Assert.That(File.Exists(manifest)).IsFalse();
        } finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Install_preserves_user_authored_blocks() {
        var dir  = TempDir();
        var path = Path.Combine(dir, "hooks.json");
        try {
            var existing = new JsonObject {
                ["my-guard"] = new JsonObject { ["PreToolUse"] = new JsonArray() }
            };
            await File.WriteAllTextAsync(path, existing.ToJsonString());

            AntigravityHooksInstaller.Install(path);

            var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            await Assert.That(root.ContainsKey("my-guard")).IsTrue();
            await Assert.That(root.ContainsKey(AntigravityHooks.BlockName)).IsTrue();
        } finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Remove_strips_only_kcap_block_and_marker() {
        var dir  = TempDir();
        var path = Path.Combine(dir, "hooks.json");
        try {
            await File.WriteAllTextAsync(path, new JsonObject {
                ["my-guard"] = new JsonObject()
            }.ToJsonString());
            AntigravityHooksInstaller.Install(path);

            AntigravityHooksInstaller.Remove(path);

            var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            await Assert.That(root.ContainsKey(AntigravityHooks.BlockName)).IsFalse();
            await Assert.That(root.ContainsKey("my-guard")).IsTrue();
            await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsFalse();
        } finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public async Task Install_backs_up_malformed_json_then_writes_valid() {
        var dir  = TempDir();
        var path = Path.Combine(dir, "hooks.json");
        try {
            await File.WriteAllTextAsync(path, "{ this is not json");

            AntigravityHooksInstaller.Install(path);

            await Assert.That(File.Exists(path + ".bak")).IsTrue();
            var root = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject;
            await Assert.That(root).IsNotNull();
            await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsTrue();
        } finally { Directory.Delete(dir, recursive: true); }
    }
}

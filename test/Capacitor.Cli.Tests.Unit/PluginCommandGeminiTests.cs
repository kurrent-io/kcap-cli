using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit;

// `plugin install/remove --gemini` also (un)registers the kcap MCP servers and installs the kcap
// steering-instructions block. Unlike Copilot (dedicated mcp-config.json), Gemini's MCP servers live
// in the SHARED ~/.gemini/settings.json (McpConfigShape.Standard → no per-entry `type`), the same file
// the hooks merge into — so the writer must be non-destructive. Instructions live in a SEPARATE global
// context file, ~/.gemini/GEMINI.md. These tests root an explicit PluginEnvironment at a FakeUserHome
// and clear GEMINI_CLI_HOME for the test's duration so GeminiPaths resolves under the fake home. The
// `--if-installed` refresh branch is used (hooks pre-seeded) so the "kcap on PATH" fresh-install
// precheck never runs.
[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandGeminiTests {
    [Test]
    public async Task install_gemini_registers_mcp_servers_into_shared_settings_preserving_user_config() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Installed-but-stale hooks so `--if-installed` refreshes (and registers MCP).
        PluginCommand.InstallGeminiHooks(env.GeminiSettingsJson);   // writes `hooks` block + marker
        GeminiHooksInstaller.DeleteMarker(env.GeminiSettingsJson);  // stale → refresh rewrites + registers

        // Splice a user-authored MCP server and an unrelated top-level setting into the shared file;
        // both must survive registration (non-destructive merge into settings.json).
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.GeminiSettingsJson))!.AsObject();
        seeded["theme"] = "dark";
        seeded["mcpServers"] = new JsonObject {
            ["my-tool"] = JsonNode.Parse("""{"command":"my-tool","args":["serve"]}""")
        };
        await File.WriteAllTextAsync(env.GeminiSettingsJson, seeded.ToJsonString());

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--gemini", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.GeminiSettingsJson))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(servers["kcap-review"]!["type"]).IsNull();  // Standard shape: no `type`
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-sessions");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-flows");
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull();  // user server preserved
        await Assert.That(root["hooks"]).IsNotNull();       // hooks block preserved
        await Assert.That(root["theme"]!.GetValue<string>()).IsEqualTo("dark");  // unrelated setting preserved
    }

    [Test]
    public async Task install_gemini_skip_mcp_flag_leaves_settings_without_mcp_servers() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallGeminiHooks(env.GeminiSettingsJson);
        GeminiHooksInstaller.DeleteMarker(env.GeminiSettingsJson);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--gemini", "--if-installed", "--skip-gemini-mcp"], env);
        await Assert.That(exit).IsEqualTo(0);

        // settings.json exists (hooks) but no MCP servers were written.
        var root = JsonNode.Parse(await File.ReadAllTextAsync(env.GeminiSettingsJson))!.AsObject();
        await Assert.That(root["mcpServers"]).IsNull();
    }

    [Test]
    public async Task install_gemini_if_installed_does_not_write_anything_when_never_opted_in() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // No hooks/marker seeded → --if-installed no-ops before touching settings.json OR GEMINI.md.
        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--gemini", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.GeminiSettingsJson)).IsFalse();
        await Assert.That(File.Exists(env.GeminiInstructionsMd)).IsFalse();
    }

    [Test]
    public async Task install_gemini_if_installed_heals_mcp_and_instructions_when_hooks_current() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Hooks installed AND marker already current → the refresh must NOT rewrite hooks, but must
        // still register the MCP servers (into settings.json) + install the instructions (GEMINI.md).
        PluginCommand.InstallGeminiHooks(env.GeminiSettingsJson);  // writes hooks + current marker

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--gemini", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(env.GeminiSettingsJson))!.AsObject()["mcpServers"]!.AsObject();
        await Assert.That(servers.Select(kv => kv.Key)).Contains("kcap-review");
        await Assert.That(File.Exists(env.GeminiInstructionsMd)).IsTrue();
    }

    [Test]
    public async Task install_gemini_if_installed_heals_instructions_when_settings_unparseable() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Marker present (→ IsInstalled true) but stale (→ hooksCurrent false), and settings.json is
        // malformed so BOTH the hooks rewrite AND MCP registration fail-closed (they share the file and
        // must leave it untouched). Instructions live in a SEPARATE GEMINI.md, so they still heal.
        Directory.CreateDirectory(GeminiPaths.Root(env.HomeDirectory));
        await File.WriteAllTextAsync(
            Path.Combine(GeminiPaths.Root(env.HomeDirectory), GeminiHooksInstaller.MarkerFileName), "0.0.0-stale");
        await File.WriteAllTextAsync(env.GeminiSettingsJson, "{ not valid json");

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--gemini", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);  // refresh swallows the hook/MCP failures on the shared file

        await Assert.That(await File.ReadAllTextAsync(env.GeminiSettingsJson)).IsEqualTo("{ not valid json"); // untouched
        await Assert.That(File.Exists(env.GeminiInstructionsMd)).IsTrue();                                     // instructions healed
        await Assert.That(await File.ReadAllTextAsync(env.GeminiInstructionsMd)).Contains("Prefer kcap tools");
    }

    [Test]
    public async Task remove_gemini_unregisters_mcp_servers_preserving_user_entries() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        // Seed settings.json as a prior install would (kcap servers + ownership marker), then splice in
        // a user-authored server + an unrelated top-level setting that must survive removal.
        JsonMcpConfigWriter.Register(env.GeminiSettingsJson, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("gemini"));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(env.GeminiSettingsJson))!.AsObject();
        seeded["theme"] = "dark";
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(env.GeminiSettingsJson, seeded.ToJsonString());

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--gemini"], env);
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.GeminiSettingsJson))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(keys).DoesNotContain("kcap-sessions");
        await Assert.That(keys).DoesNotContain("kcap-flows");
        await Assert.That(keys).DoesNotContain("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull();                        // user server preserved
        await Assert.That(root["theme"]!.GetValue<string>()).IsEqualTo("dark");   // unrelated setting preserved
    }

    [Test]
    public async Task remove_gemini_retains_marker_on_failed_unregister_then_retry_removes_entries() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        JsonMcpConfigWriter.Register(env.GeminiSettingsJson, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("gemini"));
        var installed = await File.ReadAllTextAsync(env.GeminiSettingsJson); // valid content to restore after the "fix"
        await Assert.That(new McpMarker("gemini").Owned(env.GeminiSettingsJson).ToArray()).IsNotEmpty();

        // settings.json is temporarily malformed → Unregister fails-closed.
        await File.WriteAllTextAsync(env.GeminiSettingsJson, "{ not valid json");

        var failExit = await PluginCommand.HandleAsync(["plugin", "remove", "--gemini"], env);
        await Assert.That(failExit).IsEqualTo(1);                                                        // failed unregister propagates
        await Assert.That(new McpMarker("gemini").Owned(env.GeminiSettingsJson).ToArray()).IsNotEmpty(); // marker RETAINED for retry

        // User fixes the file (kcap entries intact); the retry now succeeds and cleans up.
        await File.WriteAllTextAsync(env.GeminiSettingsJson, installed);
        var retryExit = await PluginCommand.HandleAsync(["plugin", "remove", "--gemini"], env);
        await Assert.That(retryExit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(env.GeminiSettingsJson))!.AsObject();
        var servers = root["mcpServers"] as JsonObject;
        var keys    = servers?.Select(kv => kv.Key).ToArray() ?? [];
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(new McpMarker("gemini").Owned(env.GeminiSettingsJson).ToArray()).IsEmpty();  // marker cleared after clean removal
    }

    [Test]
    public async Task install_gemini_installs_instructions_preserving_user_content() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallGeminiHooks(env.GeminiSettingsJson);
        GeminiHooksInstaller.DeleteMarker(env.GeminiSettingsJson);

        // A pre-existing user GEMINI.md that must survive.
        Directory.CreateDirectory(Path.GetDirectoryName(env.GeminiInstructionsMd)!);
        await File.WriteAllTextAsync(env.GeminiInstructionsMd, "# My rules\n\nAlways use tabs.\n");

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--gemini", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.GeminiInstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");                       // user content preserved
        await Assert.That(content).Contains(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).Contains("Prefer kcap tools");
    }

    [Test]
    public async Task install_gemini_skip_instructions_flag_leaves_gemini_md_untouched() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        PluginCommand.InstallGeminiHooks(env.GeminiSettingsJson);
        GeminiHooksInstaller.DeleteMarker(env.GeminiSettingsJson);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--gemini", "--if-installed", "--skip-gemini-instructions"], env);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(env.GeminiInstructionsMd)).IsFalse();
    }

    [Test]
    public async Task remove_gemini_strips_instructions_block_keeping_user_content() {
        using var _    = new EnvScope("GEMINI_CLI_HOME", null);
        using var home = new FakeUserHome();
        var env = TestEnv(home.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(env.GeminiInstructionsMd)!);
        await File.WriteAllTextAsync(env.GeminiInstructionsMd, "# My rules\n\nAlways use tabs.\n");
        AgentInstructionsWriter.Write(env.GeminiInstructionsMd, KcapAgentInstructions.Body);

        var exit = await PluginCommand.HandleAsync(["plugin", "remove", "--gemini"], env);
        await Assert.That(exit).IsEqualTo(0);

        var content = await File.ReadAllTextAsync(env.GeminiInstructionsMd);
        await Assert.That(content).Contains("Always use tabs.");
        await Assert.That(content).DoesNotContain(AgentInstructionsWriter.BeginMarker);
        await Assert.That(content).DoesNotContain("Prefer kcap tools");
    }

    static PluginEnvironment TestEnv(string fakeHome) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    );

    // Sets an env var for the test's lifetime and restores the previous value on Dispose.
    // Used to clear GEMINI_CLI_HOME so GeminiPaths resolves under the fake home.
    sealed class EnvScope : IDisposable {
        readonly string  _key;
        readonly string? _prev;
        public EnvScope(string key, string? value) {
            _key  = key;
            _prev = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }
        public void Dispose() => Environment.SetEnvironmentVariable(_key, _prev);
    }
}

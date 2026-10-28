using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginCommandCursorTests {
    [Test]
    public async Task install_cursor_if_installed_noops_when_marker_absent() {
        using var tmp = new TempHome();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");

        var exit = await new PluginCommand(TestEnv(tmp.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.Exists(hooksPath)).IsFalse();
    }

    [Test]
    public async Task install_cursor_if_installed_short_circuits_on_same_version_marker() {
        using var tmp = new TempHome();
        var hooksPath = Path.Combine(tmp.Path, "hooks.json");
        CursorHooksInstaller.WriteMarker(hooksPath);
        File.WriteAllText(hooksPath, "{}");

        var marker = CursorHooksInstaller.ReadMarker(hooksPath);
        await Assert.That(marker).IsEqualTo(CapacitorVersion.Current());

        var exit = await new PluginCommand(TestEnv(tmp.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--cursor-hooks-path", hooksPath]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(hooksPath)).IsEqualTo("{}");
    }

    /// <summary>The upgrade-time refresh points every kcap server at the current binary, including
    /// entries an older install left on a binary that is gone and that kcap holds no ownership record
    /// for, and leaves the user's own servers alone. Seeding hooks.json is what makes the refresh run.</summary>
    [Test]
    public async Task install_cursor_refresh_points_every_kcap_server_at_the_current_binary() {
        using var fakeHome = new TempHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var hooksPath = System.IO.Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);

        // A kcap binary that still exists is never taken over, so the stale one must be absent
        // whatever the machine has installed.
        var goneBinary = fakeHome.PathTo("uninstalled", "bin", "kcap");
        var mcpPath    = System.IO.Path.Combine(cursorDir, "mcp.json");
        await File.WriteAllTextAsync(mcpPath, new JsonObject {
            ["mcpServers"] = new JsonObject {
                ["my-tool"]        = new JsonObject { ["command"] = "my-tool",  ["args"] = new JsonArray("serve") },
                ["kcap-review"]    = new JsonObject { ["command"] = "kcap",     ["args"] = new JsonArray("mcp", "review") },
                ["kcap-workitems"] = new JsonObject { ["command"] = goneBinary, ["args"] = new JsonArray("mcp", "workitems") },
            },
        }.ToJsonString());

        var exit = await new PluginCommand(TestEnv(fakeHome.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        var servers = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!["mcpServers"]!.AsObject();
        foreach (var server in KcapMcpServers.All)
            await Assert.That(servers[server.Name]?["command"]?.GetValue<string>()).IsEqualTo(TestBinaryPath);
        await Assert.That(servers["my-tool"]!["command"]!.GetValue<string>()).IsEqualTo("my-tool");
    }

    [Test]
    public async Task install_cursor_if_installed_does_not_write_mcp_json_when_never_opted_in() {
        using var fakeHome = new TempHome();

        // Hooks were never installed, so the refresh-only postinstall path
        // no-ops before ever touching hooks.json OR mcp.json.
        var exit = await new PluginCommand(TestEnv(fakeHome.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        var mcpPath = System.IO.Path.Combine(fakeHome.Path, ".cursor", "mcp.json");
        await Assert.That(File.Exists(mcpPath)).IsFalse();
    }

    [Test]
    public async Task install_cursor_skip_cursor_mcp_flag_leaves_mcp_json_untouched() {
        using var fakeHome = new TempHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var hooksPath = System.IO.Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);

        var exit = await new PluginCommand(TestEnv(fakeHome.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "install", "--cursor", "--if-installed", "--skip-cursor-mcp"]);
        await Assert.That(exit).IsEqualTo(0);

        var mcpPath = System.IO.Path.Combine(cursorDir, "mcp.json");
        await Assert.That(File.Exists(mcpPath)).IsFalse();
    }

    [Test]
    public async Task remove_cursor_unregisters_mcp_servers_preserving_user_entries() {
        using var fakeHome = new TempHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        var hooksPath = System.IO.Path.Combine(cursorDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"command":"kcap hook --cursor"}]}}
            """);

        // Seed mcp.json exactly as a prior real install would have (so the
        // sidecar ownership marker exists), then splice in a user-authored
        // server that must survive removal.
        var mcpPath = System.IO.Path.Combine(cursorDir, "mcp.json");
        JsonMcpConfigWriter.Register(mcpPath, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("cursor", fakeHome.Home));
        var seeded = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        seeded["mcpServers"]!["my-tool"] = JsonNode.Parse("""{"command":"my-tool","args":["serve"]}""");
        await File.WriteAllTextAsync(mcpPath, seeded.ToJsonString());

        var exit = await new PluginCommand(TestEnv(fakeHome.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(
            ["plugin", "remove", "--cursor"]);
        await Assert.That(exit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        var servers = root["mcpServers"]!.AsObject();
        var keys    = servers.Select(kv => kv.Key).ToArray();
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(keys).DoesNotContain("kcap-sessions");
        await Assert.That(keys).DoesNotContain("kcap-flows");
        await Assert.That(keys).DoesNotContain("kcap-memory");
        await Assert.That(servers["my-tool"]).IsNotNull(); // user server preserved
    }

    [Test]
    public async Task remove_cursor_retains_marker_on_failed_unregister_then_retry_removes_entries() {
        using var fakeHome = new TempHome();
        var cursorDir = System.IO.Path.Combine(fakeHome.Path, ".cursor");
        Directory.CreateDirectory(cursorDir);

        // A prior real install: kcap entries + sidecar ownership marker.
        var mcpPath = System.IO.Path.Combine(cursorDir, "mcp.json");
        JsonMcpConfigWriter.Register(mcpPath, KcapMcpServers.All, McpConfigShape.Standard, cwd: null, new McpMarker("cursor", fakeHome.Home));
        var installed = await File.ReadAllTextAsync(mcpPath); // valid content to restore after the "fix"
        await Assert.That(new McpMarker("cursor", fakeHome.Home).Owned(mcpPath).ToArray()).IsNotEmpty();

        // The config is temporarily malformed/unreadable → Unregister fails-closed.
        await File.WriteAllTextAsync(mcpPath, "{ not valid json");

        var failExit = await new PluginCommand(TestEnv(fakeHome.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "remove", "--cursor"]);
        await Assert.That(failExit).IsEqualTo(1);                                          // failed MCP unregister propagates
        await Assert.That(new McpMarker("cursor", fakeHome.Home).Owned(mcpPath).ToArray()).IsNotEmpty();  // marker RETAINED for retry

        // User fixes the file (kcap entries intact); the retry now succeeds and cleans up.
        await File.WriteAllTextAsync(mcpPath, installed);
        var retryExit = await new PluginCommand(TestEnv(fakeHome.Path), workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(["plugin", "remove", "--cursor"]);
        await Assert.That(retryExit).IsEqualTo(0);

        var root    = JsonNode.Parse(await File.ReadAllTextAsync(mcpPath))!.AsObject();
        var servers = root["mcpServers"] as JsonObject;
        var keys    = servers?.Select(kv => kv.Key).ToArray() ?? [];
        await Assert.That(keys).DoesNotContain("kcap-review");
        await Assert.That(new McpMarker("cursor", fakeHome.Home).Owned(mcpPath).ToArray()).IsEmpty();     // marker cleared after clean removal
    }

    // Deterministic native-binary path: registration writes the resolved binary as the command
    // (default: the running process), so tests inject their own value and assert that,
    // never blessing whatever executable happens to run the suite.
    internal const string TestBinaryPath = "/opt/kcap-test/bin/kcap";

    static PluginEnvironment TestEnv(string fakeHome) => new(
        Home:     new(fakeHome),
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => null,
        Stdout:            TextWriter.Null,
        Stderr:            TextWriter.Null
    ) {
        Harnesses = TestHarnesses.Under(new(fakeHome)),
        Binaries  = TestBinaries.None,
        ResolveMcpBinaryPath = () => TestBinaryPath
    };

}

using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginCommandStrictRefreshTests {
    [TempHome] public required TempHome Home { get; init; }

    PluginEnvironment Environment(StringWriter stderr) => new(
        Home: Home.Home,
        Profiles: new ProfileConfig(),
        ResolvePluginPath: () => null,
        Stdout: TextWriter.Null,
        Stderr: stderr
    ) {
        Harnesses = TestHarnesses.Under(Home.Home),
        Binaries = TestBinaries.None,
        ResolveMcpBinaryPath = () => "/Applications/kcap.app/Contents/MacOS/kcap"
    };

    Task<int> Run(StringWriter stderr, params string[] args) =>
        new PluginCommand(Environment(stderr), new WorkingDirectory(Home.Path)).HandleAsync(args);

    [Test]
    [Arguments("--codex", "", ".kcap-hooks-version")]
    [Arguments("--pi", "--pi-extension-path", ".kcap-extension-version")]
    [Arguments("--opencode", "--opencode-plugin-path", ".kcap-extension-version")]
    [Arguments("--copilot", "--copilot-hooks-path", ".kcap-hooks-version")]
    [Arguments("--antigravity", "--antigravity-hooks-path", ".kcap-hooks-version")]
    [Arguments("--gemini", "--gemini-settings-path", ".kcap-hooks-version")]
    [Arguments("--mistral-vibe", "--mistral-vibe-hooks-path", ".kcap-hooks-version")]
    [Arguments("--muse", "--muse-settings-path", ".kcap-hooks-version")]
    public async Task Strict_refresh_reports_a_failed_installed_hook_without_failing_normal_refresh(
            string vendor, string pathFlag, string marker) {
        var blocked = Home.CreateDir(".codex", "hooks.json");
        Home.CreateFile([".codex", marker], "0.0.0");
        var args = new List<string> { "plugin", "install", vendor, "--if-installed" };
        if (pathFlag.Length > 0) args.AddRange([pathFlag, blocked]);
        var stderr = new StringWriter();

        await Assert.That(await Run(stderr, [.. args])).IsEqualTo(0);
        await Assert.That(await Run(stderr, [.. args, "--strict-refresh"])).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("could not refresh");
        await Assert.That(Directory.Exists(blocked)).IsTrue();
    }

    [Test]
    public async Task Strict_refresh_reports_a_failed_Grok_hook_write() {
        var hooks = Home.CreateFile(".grok/hooks.json", """{"hooks":{"SessionStart":"kcap hook --grok"}}""");
        Home.CreateDir(".grok", ".kcap-hooks-version");
        var stderr = new StringWriter();

        await Assert.That(await Run(stderr, "plugin", "install", "--grok", "--grok-hooks-path", hooks,
            "--if-installed")).IsEqualTo(0);
        await Assert.That(await Run(stderr, "plugin", "install", "--grok", "--grok-hooks-path", hooks,
            "--if-installed", "--strict-refresh")).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("Could not write Grok hooks");
    }

    [Test]
    public async Task Strict_refresh_reports_a_failed_Pi_MCP_bridge_write() {
        Home.CreateFile(".pi/agent/extensions/.kcap-extension-version", "0.0.0");
        Home.CreateDir(".pi", "agent", "extensions", "kcap-mcp.ts");
        var stderr = new StringWriter();

        await Assert.That(await Run(stderr, "plugin", "install", "--pi", "--if-installed", "--strict-refresh")).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("could not write the Pi MCP extension");
    }

    [Test]
    public async Task Strict_refresh_reports_a_failed_instruction_write() {
        Home.CreateFile(".pi/agent/extensions/.kcap-extension-version", "0.0.0");
        Home.CreateDir(".pi", "agent", "AGENTS.md");
        var stderr = new StringWriter();

        await Assert.That(await Run(stderr, "plugin", "install", "--pi", "--if-installed", "--strict-refresh")).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("could not update");
    }

    [Test]
    public async Task A_symlinked_Codex_MCP_config_reports_failure_without_touching_its_target() {
        if (OperatingSystem.IsWindows()) return;
        var config = Home.PathTo(".codex", "config.toml");
        CodexConfigToml.RegisterKcapMcpServers(config, () => "/old/kcap");
        var original = File.ReadAllText(config);
        var target = Home.PathTo("original.toml");
        File.Move(config, target);
        File.CreateSymbolicLink(config, target);
        var stderr = new StringWriter();

        await Assert.That(await Run(stderr, "plugin", "install", "--codex", "--if-installed")).IsEqualTo(0);
        await Assert.That(await Run(stderr, "plugin", "install", "--codex", "--if-installed", "--strict-refresh")).IsEqualTo(1);
        await Assert.That(stderr.ToString()).Contains("Refusing to update symlinked Codex configuration target");
        await Assert.That(File.ReadAllText(target)).IsEqualTo(original);
        await Assert.That(File.Exists(Home.PathTo(".codex", "hooks.json"))).IsFalse();
    }
}

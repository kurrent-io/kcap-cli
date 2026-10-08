using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginPathTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Bundled_CLI_finds_and_installs_the_resource_plugin_without_npm() {
        var plugin = Tmp.CreateDir("Capacitor.app", "Contents", "Resources", "kcap");
        plugin.CreateFile([".claude-plugin", "marketplace.json"], """{"name":"kcap","plugins":[{"name":"kcap","source":"./"}]}""");
        var executable = Tmp.PathTo("Capacitor.app", "Contents", "MacOS", "kcap");
        var resolved = SetupCommand.ResolvePluginPathForExecutable(executable);
        await Assert.That(resolved).IsEqualTo(plugin.Path);

        var home = new UserHome(Tmp.CreateDir("home").Path);
        var env = new PluginEnvironment(home, new ProfileConfig(), () => resolved, TextWriter.Null, TextWriter.Null) {
            Harnesses = TestHarnesses.Under(home), Binaries = TestBinaries.None,
        };
        var command = new PluginCommand(env, new WorkingDirectory(Tmp.Path));
        await Assert.That(await command.HandleAsync(["plugin", "install"])).IsEqualTo(0);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(env.Harnesses.Of<ClaudeHarness>().Paths.UserSettings));
        await Assert.That(settings!["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>()).IsEqualTo(plugin.Path);
    }

    [Test]
    public async Task Explicit_plugin_override_wins_over_bundle_resources() {
        Tmp.CreateDir("Capacitor.app", "Contents", "Resources", "kcap");
        var custom = Tmp.CreateDir("custom");
        var executable = Tmp.PathTo("Capacitor.app", "Contents", "MacOS", "kcap");
        await Assert.That(SetupCommand.ResolvePluginPathForExecutable(executable, custom.Path)).IsEqualTo(custom.Path);
    }

    [Test]
    [Arguments("wrapper/node_modules/@kurrent/kcap-osx-arm64/bin/kcap", "wrapper/kcap")]
    [Arguments("node_modules/@kurrent/kcap-osx-arm64/bin/kcap", "node_modules/@kurrent/kcap/kcap")]
    [Arguments("wrapper/bin/kcap", "wrapper/kcap")]
    public async Task Existing_CLI_package_layouts_still_resolve(string executable, string pluginPath) {
        var plugin = Tmp.CreateDir(pluginPath.Split('/'));
        await Assert.That(SetupCommand.ResolvePluginPathForExecutable(Tmp.PathTo(executable.Split('/')))).IsEqualTo(plugin.Path);
    }

    [Test]
    public async Task Missing_payload_does_not_resolve_to_an_executable() {
        Tmp.CreateFile(["Capacitor.app", "Contents", "MacOS", "kcap"], "binary");
        await Assert.That(SetupCommand.ResolvePluginPathForExecutable(Tmp.PathTo("Capacitor.app", "Contents", "MacOS", "kcap"))).IsNull();
    }
}

using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginCommandMuseTests {
    [TempHome] public required TempHome Home   { get; init; }
    [TempDir]  public required TempDir  BinDir { get; init; }

    PluginEnvironment Env(StringWriter? stderr = null) {
        var binaries = TestBinaries.Searching(BinDir, "kcap");

        return new PluginEnvironment(
            Home:              new(Home.Path),
            Profiles:          new ProfileConfig(),
            ResolvePluginPath: RepoTree.KcapDir,
            Stdout:            TextWriter.Null,
            Stderr:            stderr ?? TextWriter.Null
        ) {
            Harnesses            = TestHarnesses.Under(new(Home.Path), binaries),
            Binaries             = binaries,
            ResolveMcpBinaryPath = () => "/opt/kcap-test/bin/kcap"
        };
    }

    static Task<int> Run(PluginEnvironment env, params string[] args) =>
        new PluginCommand(env, workdir: new WorkingDirectory(AppContext.BaseDirectory)).HandleAsync(args);

    [Test]
    public async Task Install_writes_hooks_and_mcp_servers_into_settings_json_and_remove_undoes_both() {
        var env      = Env();
        var settings = env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;

        await Assert.That(await Run(env, "plugin", "install", "--muse")).IsEqualTo(0);

        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        await Assert.That(MuseHooksParser.InstalledEvents(root)).IsEquivalentTo(MuseHooksParser.MuseHookEvents);
        await Assert.That(root["mcp_servers"]!["kcap-sessions"]).IsNotNull();

        await Assert.That(await Run(env, "plugin", "remove", "--muse")).IsEqualTo(0);

        var after = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        await Assert.That(MuseHooksParser.InstalledEvents(after)).IsEmpty();
        await Assert.That(MuseSettingsJson.OwnsAnything(settings, env.Home)).IsFalse();
    }

    [Test]
    public async Task An_unparseable_settings_json_fails_the_install_and_is_left_untouched() {
        var stderr   = new StringWriter();
        var env      = Env(stderr);
        var settings = env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        const string broken = "{ \"theme\": ";
        await File.WriteAllTextAsync(settings, broken);

        await Assert.That(await Run(env, "plugin", "install", "--muse")).IsEqualTo(1);

        await Assert.That(await File.ReadAllTextAsync(settings)).IsEqualTo(broken);
        await Assert.That(stderr.ToString()).Contains("Could not install Muse hooks");
    }

    [Test]
    public async Task Muse_cannot_be_combined_with_another_target() {
        var stderr = new StringWriter();

        await Assert.That(await Run(Env(stderr), "plugin", "install", "--muse", "--codex")).IsEqualTo(1);

        await Assert.That(stderr.ToString()).Contains("--muse");
    }
}

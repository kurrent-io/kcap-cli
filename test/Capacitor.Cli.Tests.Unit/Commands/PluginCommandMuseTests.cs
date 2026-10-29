using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PluginCommandMuseTests {
    [TempHome] public required TempHome Home   { get; init; }
    [TempDir]  public required TempDir  BinDir { get; init; }

    PluginEnvironment Env(StringWriter? stderr = null, MusePaths? muse = null) {
        var binaries = TestBinaries.Searching(BinDir, "kcap");

        return new PluginEnvironment(
            Home:              new(Home.Path),
            Profiles:          new ProfileConfig(),
            ResolvePluginPath: RepoTree.KcapDir,
            Stdout:            TextWriter.Null,
            Stderr:            stderr ?? TextWriter.Null
        ) {
            Harnesses            = muse is null ? TestHarnesses.Under(new(Home.Path), binaries) : HarnessRegistry.Over(binaries, MuseHarness.Over(muse)),
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
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings, """
            {
              "theme": "dark",
              "hooks": { "SessionStart": [ { "hooks": [ { "type": "command", "command": "my-hook", "timeout": 5 } ] } ] },
              "mcp_servers": { "my-tool": { "transport": "stdio", "command": "my-tool" } }
            }
            """);

        await Assert.That(await Run(env, "plugin", "install", "--muse")).IsEqualTo(0);

        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        await Assert.That(MuseHooksParser.InstalledEvents(root)).IsEquivalentTo(MuseHooksParser.MuseHookEvents);
        await Assert.That(root["mcp_servers"]!["kcap-sessions"]).IsNotNull();

        await Assert.That(await Run(env, "plugin", "remove", "--muse")).IsEqualTo(0);

        var after = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        await Assert.That(MuseHooksParser.InstalledEvents(after)).IsEmpty();
        await Assert.That(MuseSettingsJson.OwnsAnything(settings, env.Home)).IsFalse();
        await Assert.That(after["theme"]!.GetValue<string>()).IsEqualTo("dark");
        await Assert.That(after["hooks"]!["SessionStart"]!.AsArray().Single()!["hooks"]![0]!["command"]!.GetValue<string>()).IsEqualTo("my-hook");
        await Assert.That(after["mcp_servers"]!["my-tool"]).IsNotNull();
        await Assert.That(after["mcp_servers"]!["kcap-sessions"]).IsNull();
    }

    [Test]
    public async Task Refresh_completes_a_partial_install_even_when_the_marker_is_current() {
        var env      = Env();
        var settings = env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;
        await Run(env, "plugin", "install", "--muse");
        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        root["hooks"]!.AsObject().Remove("Stop");
        await File.WriteAllTextAsync(settings, root.ToJsonString());
        await Assert.That(MuseHooksInstaller.ReadMarker(settings)).IsNotNull();

        await Assert.That(await Run(env, "plugin", "install", "--muse", "--if-installed")).IsEqualTo(0);

        await Assert.That(MuseHooksInstaller.HasAllHooks(settings)).IsTrue();
    }

    [Test]
    public async Task Refresh_leaves_out_hooks_removed_by_hand() {
        var env      = Env();
        var settings = env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;
        await Run(env, "plugin", "install", "--muse");
        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        root.Remove("hooks");
        await File.WriteAllTextAsync(settings, root.ToJsonString());

        await Assert.That(await Run(env, "plugin", "install", "--muse", "--if-installed")).IsEqualTo(0);

        var after = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        await Assert.That(MuseHooksParser.InstalledEvents(after)).IsEmpty();
        await Assert.That(env.Harnesses.Of<MuseHarness>().Signals.IsWired).IsFalse();
    }

    [Test]
    public async Task Refresh_rewrites_the_hooks_when_settings_json_is_gone_but_the_marker_is_not() {
        var env      = Env();
        var settings = env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;
        await Run(env, "plugin", "install", "--muse");
        File.Delete(settings);

        await Assert.That(await Run(env, "plugin", "install", "--muse", "--if-installed")).IsEqualTo(0);

        await Assert.That(MuseHooksInstaller.HasAllHooks(settings)).IsTrue();
    }

    [Test]
    public async Task Refresh_that_cannot_complete_the_hooks_warns_skips_mcp_and_exits_zero() {
        var stderr   = new StringWriter();
        var env      = Env(stderr);
        var settings = env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;
        await Run(env, "plugin", "install", "--muse");
        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!;
        root["hooks"]!["Stop"] = "nope";
        root["mcp_servers"]!.AsObject().Remove("kcap-sessions");
        await File.WriteAllTextAsync(settings, root.ToJsonString());

        await Assert.That(await Run(env, "plugin", "install", "--muse", "--if-installed")).IsEqualTo(0);

        await Assert.That(stderr.ToString()).Contains("could not refresh Muse hooks");
        await Assert.That(((JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(settings))!)["mcp_servers"]!["kcap-sessions"]).IsNull();
    }

    [Test]
    public async Task A_hooks_value_kcap_will_not_overwrite_fails_the_install_and_skips_mcp() {
        var stderr   = new StringWriter();
        var env      = Env(stderr);
        var settings = env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        const string content = "{\"schema_version\":1,\"hooks\":\"nope\"}";
        await File.WriteAllTextAsync(settings, content);

        await Assert.That(await Run(env, "plugin", "install", "--muse")).IsEqualTo(1);

        await Assert.That(await File.ReadAllTextAsync(settings)).IsEqualTo(content);
        await Assert.That(stderr.ToString()).Contains("Could not install Muse hooks");
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

    /// <summary>Muse's hooks run with no <c>XDG_DATA_HOME</c>, so a custom data root reaches them only
    /// through the file the install writes.</summary>
    [Test]
    public async Task Install_under_a_custom_data_root_persists_it_and_remove_clears_it() {
        string data = Home.CreateDir("custom-data");
        var env  = Env(muse: new MusePaths(Home, null, data));

        await Assert.That(await Run(env, "plugin", "install", "--muse", "--skip-muse-skills")).IsEqualTo(0);

        await Assert.That(MuseHarness.FromEnvironment(Home, _ => null).Paths.DataRoot).IsEqualTo(Path.Combine(data, "muse"));

        await Assert.That(await Run(env, "plugin", "remove", "--muse")).IsEqualTo(0);

        await Assert.That(File.Exists(MuseHookRoots.PathFor(Home))).IsFalse();
    }

    [Test]
    public async Task Install_under_the_default_roots_persists_nothing() {
        await Assert.That(await Run(Env(), "plugin", "install", "--muse", "--skip-muse-skills")).IsEqualTo(0);

        await Assert.That(File.Exists(MuseHookRoots.PathFor(Home))).IsFalse();
    }

    [Test]
    public async Task Muse_cannot_be_combined_with_another_target() {
        var stderr = new StringWriter();

        await Assert.That(await Run(Env(stderr), "plugin", "install", "--muse", "--codex")).IsEqualTo(1);

        await Assert.That(stderr.ToString()).Contains("--muse");
    }
}

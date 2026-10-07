using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Toml;
using Tomlyn.Model;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.MistralVibe;

public class MistralVibeConfigTomlTests {
    [TempDir] public required TempDir Tmp { get; init; }

    // A resolver standing in for the installed kcap binary; its basename is what proves ownership.
    static string? Kcap() => Path.Combine("usr", "bin", "kcap");

    static TomlTableArray Servers(string path) => (TomlTableArray)TomlConfigFile.Read(path)!["mcp_servers"];

    [Test]
    public async Task Register_writes_a_stdio_entry_per_server_and_is_idempotent() {
        var path = Tmp.PathTo("config.toml");

        await Assert.That(MistralVibeConfigToml.RegisterKcapMcpServers(path, Kcap)).IsEqualTo(TomlConfigFile.Outcome.Updated);
        await Assert.That(MistralVibeConfigToml.OwnsAnything(path)).IsTrue();

        var servers = Servers(path);
        await Assert.That(servers.Count).IsEqualTo(KcapMcpServers.All.Count);
        await Assert.That(servers.All(s => s.TryGetValue("transport", out var t) && t is "stdio")).IsTrue();

        await Assert.That(MistralVibeConfigToml.RegisterKcapMcpServers(path, Kcap)).IsEqualTo(TomlConfigFile.Outcome.Unchanged);
    }

    [Test]
    public async Task Unregister_removes_ours_and_preserves_a_user_server() {
        var path = Tmp.CreateFile("config.toml",
            "[[mcp_servers]]\nname = \"mine\"\ntransport = \"stdio\"\ncommand = \"foo\"\n");

        MistralVibeConfigToml.RegisterKcapMcpServers(path, Kcap);
        await Assert.That(MistralVibeConfigToml.UnregisterKcapMcpServers(path)).IsEqualTo(TomlConfigFile.Outcome.Updated);

        var servers = Servers(path);
        await Assert.That(servers.Count).IsEqualTo(1);
        await Assert.That(servers[0]["name"]).IsEqualTo("mine");
    }

    [Test]
    public async Task A_user_server_sharing_a_kcap_name_but_not_the_kcap_command_is_left_alone() {
        var path = Tmp.CreateFile("config.toml",
            "[[mcp_servers]]\nname = \"kcap-review\"\ntransport = \"stdio\"\ncommand = \"notkcap\"\n");

        await Assert.That(MistralVibeConfigToml.UnregisterKcapMcpServers(path)).IsEqualTo(TomlConfigFile.Outcome.Unchanged);
        await Assert.That(Servers(path).Count).IsEqualTo(1);
    }
}

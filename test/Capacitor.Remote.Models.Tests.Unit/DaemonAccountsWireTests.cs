using System.Text.Json;

namespace Capacitor.Remote.Models.Tests.Unit;

public class DaemonAccountsWireTests {
    const string ListedDaemonWithoutAccountKeys = """
        {"name":"box","platform":"linux","repo_paths":["/repo"],"connected":true,"owner_user_id":"github:1","version":"1.2.3","supported_vendors":["claude","codex"],"machine_id":"machine-1","vendor_models":{"claude":[{"value":"opus","label":"Opus"}]}}
        """;

    [Test]
    public async Task DaemonInfo_binds_account_support_and_per_account_models() {
        const string json = """
            {"name":"box","platform":"linux","repo_paths":["/repo"],"connected":true,"owner_user_id":"github:1","version":"1.2.3","supported_vendors":["claude","codex"],"machine_id":"machine-1","vendor_models":{"claude":[{"value":"opus","label":"Opus"}]},"supports_account_launches":true,"vendor_models_by_account":{"c1":[{"value":"gpt-5","label":"GPT-5"}]}}
            """;

        var info = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.DaemonInfo)!;

        await Assert.That(info.SupportsAccountLaunches).IsTrue();
        await Assert.That(info.VendorModelsByAccount!["c1"].Single().Value).IsEqualTo("gpt-5");
        await Assert.That(info.VendorModelsByAccount["c1"].Single().Label).IsEqualTo("GPT-5");
    }

    [Test]
    public async Task DaemonInfo_from_an_older_server_binds_false_and_null() {
        var info = JsonSerializer.Deserialize(ListedDaemonWithoutAccountKeys, RemoteModelsJsonContext.Default.DaemonInfo)!;

        await Assert.That(info.SupportsAccountLaunches).IsFalse();
        await Assert.That(info.VendorModelsByAccount).IsNull();
    }

    [Test]
    public async Task AgentInstance_binds_the_account_fields() {
        const string json = """
            {"agent_id":"a","status":"running","account_directory":"/u/.claude-work","account_id":"a1","account_label":"work"}
            """;

        var agent = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.AgentInstanceDto)!;
        await Assert.That(agent.AccountDirectory).IsEqualTo("/u/.claude-work");
        await Assert.That(agent.AccountId).IsEqualTo("a1");
        await Assert.That(agent.AccountLabel).IsEqualTo("work");

        var plain = JsonSerializer.Deserialize("""{"agent_id":"a","status":"running"}""", RemoteModelsJsonContext.Default.AgentInstanceDto)!;
        await Assert.That(plain.AccountDirectory).IsNull();
        await Assert.That(plain.AccountId).IsNull();
        await Assert.That(plain.AccountLabel).IsNull();
    }

    [Test]
    public async Task DaemonAccounts_binds_the_servers_row() {
        const string json = """
            {"daemon_name":"box","host_id":"h1","revision":7,"accounts":[{"id":"a1","vendor":"claude","directory":"/u/.claude-work","directory_key":"/u/.claude-work","label":"work","recording_state":"recording"}],"defaults":[{"vendor":"claude","directory":"/u/.claude","directory_key":"/u/.claude"}]}
            """;

        var row = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.DaemonAccountsDto)!;

        await Assert.That(row.DaemonName).IsEqualTo("box");
        await Assert.That(row.HostId).IsEqualTo("h1");
        await Assert.That(row.Revision).IsEqualTo(7L);

        var account = row.Accounts!.Single();
        await Assert.That(account.Id).IsEqualTo("a1");
        await Assert.That(account.Vendor).IsEqualTo("claude");
        await Assert.That(account.Directory).IsEqualTo("/u/.claude-work");
        await Assert.That(account.DirectoryKey).IsEqualTo("/u/.claude-work");
        await Assert.That(account.Label).IsEqualTo("work");
        await Assert.That(account.RecordingState).IsEqualTo("recording");

        var directory = row.Defaults!.Single();
        await Assert.That(directory.Vendor).IsEqualTo("claude");
        await Assert.That(directory.Directory).IsEqualTo("/u/.claude");
        await Assert.That(directory.DirectoryKey).IsEqualTo("/u/.claude");
    }
}

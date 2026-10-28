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
    public async Task AgentInstance_binds_the_terminal_dialog() {
        const string json = """
            {"agent_id":"a","status":"running","terminal_dialog":{"heading":"Allow?","body":"b","options":["No","Yes"],"selected":0,"screen":"s"}}
            """;

        var dialog = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.AgentInstanceDto)!.TerminalDialog!;
        await Assert.That(dialog.Heading).IsEqualTo("Allow?");
        await Assert.That(dialog.Body).IsEqualTo("b");
        await Assert.That(dialog.Options).IsEquivalentTo(new[] { "No", "Yes" });
        await Assert.That(dialog.Selected).IsEqualTo(0);
        await Assert.That(dialog.Screen).IsEqualTo("s");

        var plain = JsonSerializer.Deserialize("""{"agent_id":"a","status":"running"}""", RemoteModelsJsonContext.Default.AgentInstanceDto)!;
        await Assert.That(plain.TerminalDialog).IsNull();
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

    [Test]
    public async Task ListedAccount_from_an_older_server_binds_null_identity_and_reading() {
        const string json = """
            {"id":"a1","vendor":"claude","directory":"/u/.claude-work","directory_key":"/u/.claude-work","label":"work","recording_state":"recording"}
            """;

        var account = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.ListedAccountDto)!;

        await Assert.That(account.Email).IsNull();
        await Assert.That(account.Plan).IsNull();
        await Assert.That(account.Generation).IsEqualTo(0L);
        await Assert.That(account.RefreshedAt).IsNull();
        await Assert.That(account.Reading).IsNull();
    }

    [Test]
    public async Task ListedAccount_binds_reading_and_identity() {
        const string json = """
            {"id":"x1","vendor":"codex","directory":"/u/.codex-b","directory_key":"/u/.codex-b","label":"spare","recording_state":"recording",
             "email":"me@example.com","org_id":"org-1","org_name":"Acme","plan":"pro","vendor_account_id":"acct-1","sign_in":"chatgpt",
             "generation":3,"refreshed_at":"2026-10-26T12:00:00+00:00","refresh_error":"timeout","limits_error":"unsupported",
             "reading":{"generation":3,
               "windows":[{"limit_id":"codex","window_minutes":300,"used_percent":42.5,"resets_at":"2026-10-26T17:00:00+00:00","confirmed_at":"2026-10-26T12:00:00+00:00"}],
               "plan":"pro","credits":{"has_credits":true,"unlimited":false,"balance":"12.50"},
               "blocked":{"blocked_at":"2026-10-26T12:00:00+00:00","cleared_at":null,
                 "constraints":[{"limit_id":null,"window_minutes":null,"resets_at":null,"signaled_at":"2026-10-26T12:00:00+00:00"}]}}}
            """;
        var at = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

        var account = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.ListedAccountDto)!;

        await Assert.That(account.Email).IsEqualTo("me@example.com");
        await Assert.That(account.OrgId).IsEqualTo("org-1");
        await Assert.That(account.OrgName).IsEqualTo("Acme");
        await Assert.That(account.Plan).IsEqualTo("pro");
        await Assert.That(account.VendorAccountId).IsEqualTo("acct-1");
        await Assert.That(account.SignIn).IsEqualTo("chatgpt");
        await Assert.That(account.Generation).IsEqualTo(3L);
        await Assert.That(account.RefreshedAt).IsEqualTo(at);
        await Assert.That(account.RefreshError).IsEqualTo("timeout");
        await Assert.That(account.LimitsError).IsEqualTo("unsupported");

        var reading = account.Reading!;
        await Assert.That(reading.Generation).IsEqualTo(3L);
        await Assert.That(reading.MetadataAt).IsNull();
        await Assert.That(reading.Plan).IsEqualTo("pro");
        await Assert.That(reading.Windows!.Single()).IsEqualTo(new ListedUsageWindowDto {
            LimitId = "codex", WindowMinutes = 300, UsedPercent = 42.5, ResetsAt = at.AddHours(5), ConfirmedAt = at,
        });
        await Assert.That(reading.Credits).IsEqualTo(new ListedCreditsDto { HasCredits = true, Unlimited = false, Balance = "12.50" });
        await Assert.That(reading.Blocked!.BlockedAt).IsEqualTo(at);
        await Assert.That(reading.Blocked.ClearedAt).IsNull();
        await Assert.That(reading.Blocked.Constraints!.Single()).IsEqualTo(new ListedBlockConstraintDto {
            LimitId = null, WindowMinutes = null, ResetsAt = null, SignaledAt = at,
        });
    }

    [Test]
    public async Task ListedReading_binds_metadataAt() {
        const string json = """{"generation":3,"plan":"pro","metadata_at":"2026-10-26T12:00:00+00:00"}""";

        var reading = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.ListedReadingDto)!;

        await Assert.That(reading.MetadataAt).IsEqualTo(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task A_window_without_a_limit_id_binds_and_the_rest_of_the_list_survives() {
        const string json = """
            {"daemon_name":"box","host_id":"h1","revision":7,"accounts":[
              {"id":"a1","vendor":"codex","directory":"/u/.codex-a","directory_key":"/u/.codex-a","label":"a","recording_state":"recording",
               "reading":{"generation":0,"windows":[{"window_minutes":300,"used_percent":10},{"limit_id":"codex","window_minutes":10080,"used_percent":20}]}},
              {"id":"a2","vendor":"claude","directory":"/u/.claude-b","directory_key":"/u/.claude-b","label":"b","recording_state":"recording"}],
             "defaults":[]}
            """;

        var row = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.DaemonAccountsDto)!;

        await Assert.That(row.Accounts!.Select(a => a.Id)).IsEquivalentTo(["a1", "a2"]);
        var windows = row.Accounts![0].Reading!.Windows!;
        await Assert.That(windows[0].LimitId).IsNull();
        await Assert.That(windows[1].LimitId).IsEqualTo("codex");
    }
}

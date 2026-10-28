using System.Text.Json;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;

namespace Capacitor.Cli.Core.Tests.Unit.Accounts;

public class AccountsSnapshotDtoTests {
    static AccountsSnapshotDto Snapshot() => new(
        "host-1", 7,
        [new AccountViewDto("a1b2", "codex", "/Users/x/.codex-work", "/users/x/.codex-work", "Work", "not_wired")],
        [new DefaultDirectoryDto("claude", "/Users/x/.claude", "/users/x/.claude")]);

    [Test]
    public async Task Snapshot_serializes_snake_case_with_every_field_present() {
        var json = JsonSerializer.Serialize(Snapshot(), AccountsSnapshotJsonContext.Default.AccountsSnapshotDto);

        await Assert.That(json).IsEqualTo(
            """{"host_id":"host-1","revision":7,"accounts":[{"id":"a1b2","vendor":"codex","directory":"/Users/x/.codex-work","directory_key":"/users/x/.codex-work","label":"Work","recording_state":"not_wired","email":null,"org_id":null,"org_name":null,"plan":null,"vendor_account_id":null,"sign_in":null,"generation":0,"refreshed_at":null,"refresh_error":null,"limits_error":null,"reading":null}],"defaults":[{"vendor":"claude","directory":"/Users/x/.claude","directory_key":"/users/x/.claude"}]}""");
    }

    [Test]
    public async Task Snapshot_round_trips() {
        var json = JsonSerializer.Serialize(Snapshot(), AccountsSnapshotJsonContext.Default.AccountsSnapshotDto);

        var back = JsonSerializer.Deserialize(json, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto)!;

        await Assert.That(back.HostId).IsEqualTo("host-1");
        await Assert.That(back.Revision).IsEqualTo(7L);
        await Assert.That(back.Accounts[0]).IsEqualTo(Snapshot().Accounts[0]);
        await Assert.That(back.Defaults[0]).IsEqualTo(Snapshot().Defaults[0]);
    }

    [Test]
    public async Task Hub_context_serializes_the_snapshot_the_same_way() {
        var viaHub    = JsonSerializer.Serialize(Snapshot(), CapacitorJsonContext.Default.AccountsSnapshotDto);
        var viaOwnCtx = JsonSerializer.Serialize(Snapshot(), AccountsSnapshotJsonContext.Default.AccountsSnapshotDto);

        await Assert.That(viaHub).IsEqualTo(viaOwnCtx);
    }

    [Test]
    public async Task Snapshot_from_slice_3_daemon_binds() {
        const string json = """
            {"host_id":"host-1","revision":7,"accounts":[{"id":"a1b2","vendor":"codex","directory":"/Users/x/.codex-work","directory_key":"/users/x/.codex-work","label":"Work","recording_state":"not_wired"}],"defaults":[]}
            """;

        var view = JsonSerializer.Deserialize(json, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto)!.Accounts.Single();

        await Assert.That(view).IsEqualTo(Snapshot().Accounts[0]);
        await Assert.That(view.Generation).IsEqualTo(0L);
        await Assert.That(view.Reading).IsNull();
        await Assert.That(view.Email).IsNull();
    }

    [Test]
    public async Task Snapshot_reading_without_metadataAt_binds_null() {
        const string json = """
            {"host_id":"host-1","revision":7,"accounts":[{"id":"a1b2","vendor":"codex","directory":"/Users/x/.codex-work","directory_key":"/users/x/.codex-work","label":"Work","recording_state":"recording",
             "reading":{"generation":0,"windows":[],"plan":"pro","credits":null,"blocked":null}}],"defaults":[]}
            """;

        var reading = JsonSerializer.Deserialize(json, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto)!.Accounts.Single().Reading!;

        await Assert.That(reading.Plan).IsEqualTo("pro");
        await Assert.That(reading.MetadataAt).IsNull();
    }

    [Test]
    public async Task New_snapshot_round_trips() {
        var at      = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        var reading = new AccountReadingDto(3,
            [new UsageWindowDto("codex", 300, 42.5, at.AddHours(5), at), new UsageWindowDto("codex", 10080, 7, null, null)],
            "pro", new CreditsDto(true, false, "12.50"),
            new BlockedMarkerDto(at, null, [new BlockConstraintDto("codex", 300, at.AddHours(5), at), new BlockConstraintDto(null, null, null, at)]),
            at);
        var view = Snapshot().Accounts[0] with {
            Email = "me@example.com", OrgId = "org-1", OrgName = "Acme", Plan = "pro", VendorAccountId = "acct-1", SignIn = "chatgpt",
            Generation = 3, RefreshedAt = at, RefreshError = "timeout", LimitsError = "unsupported", Reading = reading,
        };
        var snapshot = Snapshot() with { Accounts = [view] };

        var json = JsonSerializer.Serialize(snapshot, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto);
        var back = JsonSerializer.Deserialize(json, AccountsSnapshotJsonContext.Default.AccountsSnapshotDto)!.Accounts.Single();

        await Assert.That(back with { Reading = null }).IsEqualTo(view with { Reading = null });
        await Assert.That(ReadingMerge.Equivalent(back.Reading!, reading)).IsTrue();
        await Assert.That(JsonSerializer.Serialize(snapshot, CapacitorJsonContext.Default.AccountsSnapshotDto)).IsEqualTo(json);
    }
}

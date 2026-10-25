using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

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
            """{"host_id":"host-1","revision":7,"accounts":[{"id":"a1b2","vendor":"codex","directory":"/Users/x/.codex-work","directory_key":"/users/x/.codex-work","label":"Work","recording_state":"not_wired"}],"defaults":[{"vendor":"claude","directory":"/Users/x/.claude","directory_key":"/users/x/.claude"}]}""");
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
}

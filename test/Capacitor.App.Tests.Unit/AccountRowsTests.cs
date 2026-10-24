using Capacitor.App.Services;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class AccountRowsTests {
    static AccountViewDto Account(string id, string vendor, string directory, string label, string state = "recording") =>
        new(id, vendor, directory, directory, label, state);

    static DefaultDirectoryDto Default(string vendor, string directory) => new(vendor, directory, directory);

    [Test]
    public async Task Rows_are_the_vendors_accounts_with_the_default_badge_on_the_matching_key() {
        var snapshot = new AccountsSnapshotDto("h1", 4, [
            Account("a2", "claude", "/u/.claude-work", "work"),
            Account("a1", "claude", "/u/.claude", "home"),
            Account("a4", "claude", "/u/.claude-z", "home"),
            Account("a3", "codex", "/u/.codex", "main"),
        ], [
            Default("claude", "/u/.claude"),
            Default("codex", "/u/.codex"),
        ]);

        var rows = AccountRows.For(snapshot, "Claude");

        await Assert.That(string.Join(",", rows.Select(r => r.Id))).IsEqualTo("a1,a4,a2");
        await Assert.That(string.Join(",", rows.Select(r => r.IsDefault))).IsEqualTo("True,False,False");
        await Assert.That(rows[0]).IsEqualTo(new AccountRow("a1", "claude", "/u/.claude", "home", "recording", true));
    }

    [Test]
    public async Task An_unregistered_default_is_one_more_row_labelled_by_directory_name_and_not_wired() {
        var snapshot = new AccountsSnapshotDto("h1", 4, [Account("a1", "claude", "/u/.claude-work", "work")], [Default("claude", "/u/.claude/")]);

        var rows = AccountRows.For(snapshot, "claude");

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].IsDefault).IsFalse();
        await Assert.That(rows[1]).IsEqualTo(new AccountRow(null, "claude", "/u/.claude/", ".claude", "not_wired", true));
    }

    [Test]
    public async Task An_unregistered_default_on_a_windows_host_is_labelled_by_its_last_segment() {
        var snapshot = new AccountsSnapshotDto("h1", 1, [], [Default("codex", @"C:\Users\a\.codex\")]);

        var rows = AccountRows.For(snapshot, "codex");

        await Assert.That(rows.Single().Label).IsEqualTo(".codex");
    }

    [Test]
    public async Task A_vendor_without_a_default_has_no_badge_and_no_extra_row() {
        var snapshot = new AccountsSnapshotDto("h1", 4, [Account("a1", "claude", "/u/.claude-work", "work")], [Default("codex", "/u/.codex")]);

        var rows = AccountRows.For(snapshot, "claude");

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].IsDefault).IsFalse();
    }

    [Test]
    public async Task Only_one_row_results_from_an_empty_registry_with_a_default() {
        var snapshot = new AccountsSnapshotDto("h1", 1, [], [Default("claude", "/u/.claude")]);

        var rows = AccountRows.For(snapshot, "claude");

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Id).IsNull();
        await Assert.That(rows[0].IsDefault).IsTrue();
    }

    [Test]
    public async Task A_snapshot_with_null_arrays_yields_no_rows() {
        var snapshot = new AccountsSnapshotDto("h1", 1, null!, null!);

        await Assert.That(AccountRows.For(snapshot, "claude").Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("codex", "installed", "Needs trust", true)]
    [Arguments("claude", "installed", "Wired", false)]
    [Arguments("claude", "broken", "Broken", true)]
    [Arguments("claude", "not_wired", "Not wired", true)]
    [Arguments("claude", "recording", "Recording", false)]
    public async Task Labels_follow_the_token_and_vendor(string vendor, string token, string label, bool attention) {
        await Assert.That(RecordingStateLabels.Label(vendor, token)).IsEqualTo(label);
        await Assert.That(RecordingStateLabels.IsAttention(vendor, token)).IsEqualTo(attention);
    }

    [Test]
    public async Task An_unknown_token_is_shown_as_it_came() {
        await Assert.That(RecordingStateLabels.Label("claude", "paused")).IsEqualTo("paused");
        await Assert.That(RecordingStateLabels.IsAttention("claude", "paused")).IsFalse();
    }

    [Test]
    public async Task Only_a_codex_install_that_awaits_trust_carries_a_hint() {
        await Assert.That(RecordingStateLabels.Hint("codex", "installed", "/u/.codex-work"))
            .IsEqualTo("Trust the kcap hooks the next time you start Codex with CODEX_HOME=/u/.codex-work");
        await Assert.That(RecordingStateLabels.Hint("claude", "installed", "/u/.claude")).IsNull();
        await Assert.That(RecordingStateLabels.Hint("codex", "recording", "/u/.codex")).IsNull();
    }

    [Test]
    public async Task A_server_row_maps_to_the_snapshot_shape_with_null_arrays_as_empty() {
        var row = new DaemonAccountsDto {
            DaemonName = "box", HostId = "h1", Revision = 7,
            Accounts = [new ListedAccountDto {
                Id = "a1", Vendor = "claude", Directory = "/u/.claude-work", DirectoryKey = "/u/.claude-work",
                Label = "work", RecordingState = "installed",
            }],
            Defaults = [new ListedDefaultDirectoryDto { Vendor = "claude", Directory = "/u/.claude", DirectoryKey = "/u/.claude" }],
        };

        var snapshot = row.ToSnapshot();

        await Assert.That(snapshot.HostId).IsEqualTo("h1");
        await Assert.That(snapshot.Revision).IsEqualTo(7L);
        await Assert.That(snapshot.Accounts.Single()).IsEqualTo(Account("a1", "claude", "/u/.claude-work", "work", "installed"));
        await Assert.That(snapshot.Defaults.Single()).IsEqualTo(Default("claude", "/u/.claude"));

        var bare = new DaemonAccountsDto { DaemonName = "box", HostId = "h2", Revision = 1 }.ToSnapshot();

        await Assert.That(bare.Accounts.Length).IsEqualTo(0);
        await Assert.That(bare.Defaults.Length).IsEqualTo(0);
    }
}

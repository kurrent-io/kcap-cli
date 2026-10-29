using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.Tests.Unit;

public class AccountChooserTests {
    const string Repo    = "/r/app";
    const string Loading = "Loading accounts…";

    static readonly AccountsSnapshotDto HomeAndWork = Snapshot("h1", Account("a1", "home", "/u/.claude"), Account("a2", "work", "/u/.claude-work"));
    static readonly AccountsSnapshotDto HomeOnly    = Snapshot("h1", Account("a1", "home", "/u/.claude"));

    readonly Dictionary<(string Host, string Vendor, string Repo), string> _memory = [];
    readonly List<string> _writes = [];
    int _reads;

    static AccountViewDto Account(string id, string label, string directory, string vendor = "claude") =>
        new(id, vendor, directory, directory, label, "recording");

    static AccountsSnapshotDto Snapshot(string hostId, params AccountViewDto[] accounts) =>
        new(hostId, 1, accounts, [new("claude", "/u/.claude", "/u/.claude"), new("codex", "/u/.codex", "/u/.codex")]);

    AccountChooser NewChooser() => new(
        (host, vendor, repo) => {
            _reads++;
            return _memory.GetValueOrDefault((host, vendor, repo));
        },
        (host, vendor, repo, id) => {
            _writes.Add($"{host}/{vendor}:{repo}={id ?? "null"}");
            if (id is null) _memory.Remove((host, vendor, repo));
            else _memory[(host, vendor, repo)] = id;
        });

    AccountChooser Ready(AccountsSnapshotDto? snapshot, AccountSupport support = AccountSupport.Supported) {
        var chooser = NewChooser();
        chooser.EnterTarget(Repo, "claude");
        chooser.SetMemoryLoaded(true);
        chooser.SetSupport(support, fetchFailed: false);
        chooser.SetSnapshot(snapshot);
        return chooser;
    }

    static string Selected(AccountChooser chooser) => string.Join(",", chooser.Rows.Where(r => r.Selected).Select(r => r.Row.Id ?? "-"));

    [Test]
    public async Task A_vendor_without_accounts_is_default_with_no_section() {
        _memory[("h1", "pi", Repo)] = "p1";
        var chooser = NewChooser();
        chooser.EnterTarget(Repo, "pi");

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);

        chooser.SetMemoryLoaded(true);
        chooser.SetSupport(AccountSupport.Supported, fetchFailed: false);
        chooser.SetSnapshot(Snapshot("h1", Account("p1", "work", "/u/.pi", "pi")));
        chooser.Pick("p1");

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.SelectedAccountId).IsNull();
        await Assert.That(chooser.ShowsSection).IsFalse();
        await Assert.That(chooser.Rows.Count).IsEqualTo(0);
        await Assert.That(_reads).IsEqualTo(0);
        await Assert.That(_writes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unknown_support_is_pending_with_the_loading_text() {
        _memory[("h1", "claude", Repo)] = "a2";

        var chooser = Ready(HomeAndWork, AccountSupport.Unknown);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Pending);
        await Assert.That(chooser.IsResolved).IsFalse();
        await Assert.That(chooser.PendingText).IsEqualTo(Loading);
        await Assert.That(chooser.ShowsSection).IsTrue();
        await Assert.That(chooser.SelectedAccountId).IsNull();
        await Assert.That(Selected(chooser)).IsEqualTo("");
        await Assert.That(_memory[("h1", "claude", Repo)]).IsEqualTo("a2");
    }

    [Test]
    public async Task Unsupported_is_default_and_keeps_the_memory_entry() {
        _memory[("h1", "claude", Repo)] = "a2";

        var chooser = Ready(HomeAndWork, AccountSupport.Unsupported);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.IsResolved).IsTrue();
        await Assert.That(chooser.SelectedAccountId).IsNull();
        await Assert.That(chooser.ShowsSection).IsFalse();
        await Assert.That(chooser.PendingText).IsNull();
        await Assert.That(_writes.Count).IsEqualTo(0);
        await Assert.That(_memory[("h1", "claude", Repo)]).IsEqualTo("a2");
    }

    [Test]
    public async Task Supported_without_a_snapshot_is_pending_until_the_snapshot_arrives_then_restores() {
        _memory[("h1", "claude", Repo)] = "a2";
        var chooser = Ready(null);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Pending);
        await Assert.That(chooser.PendingText).IsEqualTo(Loading);
        await Assert.That(chooser.ShowsSection).IsTrue();
        await Assert.That(chooser.Rows.Count).IsEqualTo(0);

        var changes = 0;
        chooser.Changed += () => changes++;
        chooser.SetSnapshot(HomeAndWork);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a2");
        await Assert.That(chooser.PendingText).IsNull();
        await Assert.That(changes).IsEqualTo(1);
    }

    [Test]
    public async Task Memory_not_yet_loaded_keeps_the_target_pending() {
        _memory[("h1", "claude", Repo)] = "a2";
        var chooser = NewChooser();
        chooser.EnterTarget(Repo, "claude");
        chooser.SetSnapshot(HomeAndWork);
        chooser.SetSupport(AccountSupport.Unsupported, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Pending);
        await Assert.That(chooser.ShowsSection).IsFalse();

        chooser.SetSupport(AccountSupport.Supported, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Pending);
        await Assert.That(chooser.ShowsSection).IsTrue();
        await Assert.That(_reads).IsEqualTo(0);

        chooser.SetMemoryLoaded(true);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a2");
    }

    [Test]
    public async Task A_remembered_id_that_the_snapshot_lists_is_explicit_valid_and_selected() {
        _memory[("h1", "claude", Repo)] = "a2";

        var chooser = Ready(HomeAndWork);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);
        await Assert.That(chooser.IsResolved).IsTrue();
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a2");
        await Assert.That(chooser.SelectedLabel).IsEqualTo("work");
        await Assert.That(chooser.InvalidReason).IsNull();
        await Assert.That(Selected(chooser)).IsEqualTo("a2");
        await Assert.That(_writes.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_remembered_id_absent_from_its_hosts_snapshot_is_forgotten_and_the_default_restored() {
        _memory[("h1", "claude", Repo)] = "a9";

        var chooser = Ready(HomeAndWork);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.SelectedAccountId).IsNull();
        await Assert.That(chooser.SelectedLabel).IsNull();
        await Assert.That(Selected(chooser)).IsEqualTo("a1");
        await Assert.That(string.Join(";", _writes)).IsEqualTo("h1/claude:/r/app=null");
        await Assert.That(_memory.ContainsKey(("h1", "claude", Repo))).IsFalse();
    }

    [Test]
    public async Task A_snapshot_from_another_host_restores_nothing_and_forgets_nothing() {
        _memory[("h1", "claude", Repo)] = "a2";

        var chooser = Ready(Snapshot("h2", Account("a1", "home", "/u/.claude"), Account("a2", "work", "/u/.claude-work")));

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.SelectedAccountId).IsNull();

        chooser.SetSnapshot(Snapshot("h2", Account("a1", "home", "/u/.claude")));

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(_writes.Count).IsEqualTo(0);
        await Assert.That(_memory[("h1", "claude", Repo)]).IsEqualTo("a2");
    }

    [Test]
    public async Task A_pick_writes_memory_and_the_default_pick_removes_it() {
        var chooser = Ready(HomeAndWork);

        chooser.Pick("a2");

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a2");
        await Assert.That(_memory[("h1", "claude", Repo)]).IsEqualTo("a2");

        chooser.Pick(null);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(Selected(chooser)).IsEqualTo("a1");
        await Assert.That(_memory.ContainsKey(("h1", "claude", Repo))).IsFalse();
        await Assert.That(string.Join(";", _writes)).IsEqualTo("h1/claude:/r/app=a2;h1/claude:/r/app=null");

        chooser.SetSnapshot(null);
        chooser.Pick("a2");

        await Assert.That(_writes.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_late_snapshot_does_not_override_a_pick_made_since() {
        _memory[("h1", "claude", Repo)] = "a2";
        var chooser = Ready(null);

        chooser.Pick(null);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);

        chooser.SetSnapshot(HomeAndWork);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.SelectedAccountId).IsNull();
        await Assert.That(Selected(chooser)).IsEqualTo("a1");
    }

    [Test]
    public async Task Support_withdrawn_or_unknown_again_turns_a_pick_explicit_invalid_not_default() {
        var chooser = Ready(HomeAndWork);
        chooser.Pick("a2");

        chooser.SetSupport(AccountSupport.Unsupported, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitInvalid);
        await Assert.That(chooser.IsResolved).IsFalse();
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a2");
        await Assert.That(chooser.SelectedLabel).IsEqualTo("work — unavailable");
        await Assert.That(chooser.InvalidReason).IsEqualTo("work is not available on this machine");
        await Assert.That(chooser.PendingText).IsNull();

        chooser.SetSupport(AccountSupport.Supported, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);

        chooser.SetSupport(AccountSupport.Unknown, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitInvalid);

        chooser.Pick(null);
        chooser.SetSupport(AccountSupport.Unsupported, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);

        chooser.SetSupport(AccountSupport.Unknown, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
    }

    [Test]
    public async Task The_picked_account_removed_from_the_snapshot_is_invalid_until_it_returns() {
        var chooser = Ready(HomeAndWork);
        chooser.Pick("a2");

        chooser.SetSnapshot(HomeOnly);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitInvalid);
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a2");
        await Assert.That(chooser.Rows.Count).IsEqualTo(2);
        await Assert.That(chooser.Rows[0].Selected).IsFalse();
        await Assert.That(chooser.Rows[1]).IsEqualTo(new AccountChoice(new AccountRow("a2", "claude", "", "work", "", false), Selected: true, Removed: true));
        await Assert.That(_memory.ContainsKey(("h1", "claude", Repo))).IsFalse();

        chooser.SetSnapshot(HomeAndWork);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);
        await Assert.That(chooser.Rows.Any(r => r.Removed)).IsFalse();
        await Assert.That(Selected(chooser)).IsEqualTo("a2");

        chooser.Pick("a9");

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitInvalid);
        await Assert.That(chooser.Rows[^1].Row.Label).IsEqualTo("a9");
    }

    [Test]
    public async Task Use_default_resolves_an_invalid_pick() {
        var chooser = Ready(HomeAndWork);
        chooser.Pick("a2");
        chooser.SetSnapshot(HomeOnly);

        chooser.UseDefault();

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.IsResolved).IsTrue();
        await Assert.That(chooser.SelectedAccountId).IsNull();
        await Assert.That(chooser.SelectedLabel).IsNull();
        await Assert.That(chooser.InvalidReason).IsNull();
        await Assert.That(chooser.Rows.Count).IsEqualTo(1);
        await Assert.That(Selected(chooser)).IsEqualTo("a1");

        chooser.SetSnapshot(HomeAndWork);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
    }

    [Test]
    public async Task The_section_shows_at_two_rows_not_one_and_stays_while_invalid() {
        var chooser = Ready(HomeOnly);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.Rows.Count).IsEqualTo(1);
        await Assert.That(chooser.ShowsSection).IsFalse();

        chooser.SetSnapshot(HomeAndWork);

        await Assert.That(chooser.ShowsSection).IsTrue();

        chooser.Pick("a2");
        chooser.SetSnapshot(HomeOnly);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitInvalid);
        await Assert.That(chooser.Rows.Count).IsEqualTo(2);
        await Assert.That(chooser.ShowsSection).IsTrue();
    }

    [Test]
    public async Task Entering_another_target_re_restores_that_targets_entry() {
        _memory[("h1", "claude", Repo)] = "a2";
        _memory[("h1", "codex", Repo)]  = "c2";
        var chooser = Ready(Snapshot("h1",
            Account("a1", "home", "/u/.claude"), Account("a2", "work", "/u/.claude-work"),
            Account("c1", "main", "/u/.codex", "codex"), Account("c2", "side", "/u/.codex-side", "codex")));

        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a2");

        chooser.Pick("a1");
        chooser.EnterTarget("/r/other", "claude");

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.SelectedAccountId).IsNull();

        chooser.EnterTarget(Repo, "codex");

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("c2");
        await Assert.That(string.Join(",", chooser.Rows.Select(r => r.Row.Id))).IsEqualTo("c1,c2");

        chooser.EnterTarget(Repo, "claude");

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.ExplicitValid);
        await Assert.That(chooser.SelectedAccountId).IsEqualTo("a1");
    }

    [Test]
    public async Task Retry_is_offered_only_while_pending_after_a_failed_fetch() {
        var chooser = NewChooser();
        chooser.EnterTarget(Repo, "claude");
        chooser.SetMemoryLoaded(true);
        chooser.SetSupport(AccountSupport.Unknown, fetchFailed: false);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Pending);
        await Assert.That(chooser.OffersRetry).IsFalse();

        chooser.SetSupport(AccountSupport.Unknown, fetchFailed: true);

        await Assert.That(chooser.OffersRetry).IsTrue();

        chooser.SetSnapshot(HomeAndWork);
        chooser.SetSupport(AccountSupport.Supported, fetchFailed: true);

        await Assert.That(chooser.State).IsEqualTo(AccountSelectionState.Default);
        await Assert.That(chooser.OffersRetry).IsFalse();
    }
}

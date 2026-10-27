using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class AccountsSettingsViewModelTests {
    static AccountViewDto Account(string id, string vendor, string directory, string label, string state = "recording") =>
        new(id, vendor, directory, directory, label, state);

    static readonly AccountsSnapshotDto Snapshot = new("host-1", 3, [
        Account("a2", "claude", "/u/.claude-work", "work"),
        Account("a1", "claude", "/u/.claude", "home"),
        Account("a3", "codex", "/u/.codex-main", "main", "installed"),
    ], [new("claude", "/u/.claude", "/u/.claude"), new("codex", "/u/.codex", "/u/.codex")]);

    static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    static AccountReadingDto Reading(double percent, DateTimeOffset? confirmed, DateTimeOffset? resets, string limit = "claude", int minutes = 300) =>
        new(0, [new UsageWindowDto(limit, minutes, percent, resets, confirmed)], null, null, null);

    static readonly AttachStatus Connected = new(AttachState.Connected, null, ["accounts/1"]);

    sealed class Rig : IDisposable {
        public readonly FakeAccountsService Accounts = new();
        public readonly BehaviorSubject<AttachStatus> Status = new(Connected);
        public readonly BehaviorSubject<DateTimeOffset> Clock = new(Now);
        public readonly List<MutationRequest> Requests = [];
        public Func<MutationRequest, Task<MutationOutcome>> Outcome = _ => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        public IReadOnlyList<AccountCandidate> Found = [];
        public int Discoveries;
        public bool DiscoveredOnUiThread;
        public string? Picked;
        public int Picks;
        public readonly AccountsSettingsViewModel Vm;

        public Rig(AccountsSnapshotDto? snapshot = null) {
            if (snapshot is not null) Accounts.Publish(snapshot);
            Vm = new AccountsSettingsViewModel(Accounts,
                (request, _) => { Requests.Add(request); return Outcome(request); },
                "daemon-a", "work",
                () => {
                    Interlocked.Increment(ref Discoveries);
                    DiscoveredOnUiThread |= Dispatcher.UIThread.CheckAccess();
                    return Found;
                },
                _ => { Picks++; return Task.FromResult(Picked); },
                Status, usageClock: Clock);
        }

        public AccountSettingsRow Row(string label) => Vm.Groups.SelectMany(g => g.Rows).Single(r => r.Label == label);

        public void Dispose() => Vm.Dispose();
    }

    static bool CanRun<T>(ICommand command, T parameter) => command.CanExecute(parameter);

    static async Task WaitUntilAsync(Func<bool> ready) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    [Test]
    public Task Groups_list_each_vendors_rows_with_the_default_badge_and_the_unregistered_default_as_not_set_up() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new Rig(Snapshot);
            var vm = rig.Vm;

            await Assert.That(string.Join(",", vm.Groups.Select(g => $"{g.Vendor}:{g.VendorLabel}"))).IsEqualTo("claude:Claude Code,codex:Codex");
            var claude = vm.Groups[0].Rows;
            await Assert.That(string.Join(",", claude.Select(r => r.Label))).IsEqualTo("home,work");
            await Assert.That(claude[0]).IsEqualTo(new AccountSettingsRow("a1", "claude", "/u/.claude", "home", "recording", null, null, null, claude[0].Usage, true));
            await Assert.That(claude[0].StateLabel).IsEqualTo("Recording");
            await Assert.That(claude[0].StateIsAttention).IsFalse();
            await Assert.That(claude[1].IsDefault).IsFalse();

            var codex = vm.Groups[1].Rows;
            await Assert.That(codex.Count).IsEqualTo(2);
            await Assert.That(codex[0].StateLabel).IsEqualTo("Needs approval in Codex");
            await Assert.That(codex[0].StateIsAttention).IsTrue();
            await Assert.That(codex[0].StateHint).IsEqualTo("Start Codex once with this account and approve kcap's hooks when it asks.");
            await Assert.That(codex[0].StateCommand).IsEqualTo(RecordingStateLabels.Command("codex", "installed", "/u/.codex-main"));
            await Assert.That(codex[0].StateCommand).IsNotNull();
            var fallback = codex[1];
            await Assert.That(fallback).IsEqualTo(new AccountSettingsRow(null, "codex", "/u/.codex", ".codex", "not_wired", null, null, null, fallback.Usage, true));
            await Assert.That(fallback.IsUnregisteredDefault).IsTrue();
            await Assert.That(fallback.StateLabel).IsEqualTo("Not set up");
            await Assert.That(fallback.StateIsAttention).IsTrue();
            await Assert.That(codex[0].IsUnregisteredDefault).IsFalse();

            await Assert.That(vm.CanManage).IsTrue();
            await Assert.That(vm.ReadOnlyNotice).IsNull();
            await Assert.That(CanRun(vm.AddDefaultCommand, fallback)).IsTrue();

            var shown = vm.Groups;
            rig.Accounts.SnapshotSubject.OnNext(Snapshot with { Revision = 4 });
            await Assert.That(vm.Groups).IsSameReferenceAs(shown);
        });

    [Test]
    public Task Without_the_capability_the_tab_is_read_only_and_says_to_update_kcap() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig();
        rig.Status.OnNext(new AttachStatus(AttachState.Connected, null, ["settings/1"]));
        var vm = rig.Vm;
        var row = new AccountSettingsRow("a2", "claude", "/u/.claude-work", "work", "recording", null, null, null, new AccountUsageSlot(), false);

        await Assert.That(vm.ReadOnlyNotice).IsEqualTo("Update kcap on this machine to manage accounts");
        await Assert.That(vm.CanManage).IsFalse();
        await Assert.That(vm.Groups.Count).IsEqualTo(0);
        await Assert.That(CanRun(vm.RemoveCommand, row)).IsFalse();
        await Assert.That(CanRun(vm.AddFromPickerCommand, "claude")).IsFalse();

        await vm.RemoveCommand.Execute(row).ToTask();
        await vm.AddFromPickerCommand.Execute("claude").ToTask();

        await Assert.That(rig.Requests.Count).IsEqualTo(0);
        await Assert.That(rig.Picks).IsEqualTo(0);
    });

    [Test]
    public Task Without_a_snapshot_the_tab_says_the_daemon_must_connect() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig();
        var vm = rig.Vm;
        const string notice = "Accounts are listed once the daemon is connected";

        // Connected with accounts/1 while Supported is still false, as on a disconnect: the status decides.
        await Assert.That(vm.ReadOnlyNotice).IsEqualTo(notice);

        rig.Status.OnNext(new AttachStatus(AttachState.Unreachable, null, null));
        await Assert.That(vm.ReadOnlyNotice).IsEqualTo(notice);
        await Assert.That(vm.CanManage).IsFalse();

        rig.Status.OnNext(Connected);
        rig.Accounts.SupportedSubject.OnNext(true);
        await Assert.That(vm.ReadOnlyNotice).IsEqualTo(notice);
        await Assert.That(vm.CanManage).IsFalse();

        rig.Accounts.SnapshotSubject.OnNext(Snapshot);
        await Assert.That(vm.ReadOnlyNotice).IsNull();
        await Assert.That(vm.CanManage).IsTrue();

        rig.Accounts.SnapshotSubject.OnNext(null);
        await Assert.That(vm.ReadOnlyNotice).IsEqualTo(notice);
        await Assert.That(vm.CanManage).IsFalse();
        await Assert.That(vm.Groups.Count).IsEqualTo(0);
    });

    [Test]
    [Arguments("add candidate", MutationVerb.AccountAdd, "codex", "/u/.codex-alt", null, null)]
    [Arguments("add default", MutationVerb.AccountAdd, "codex", "/u/.codex", null, null)]
    [Arguments("add from picker", MutationVerb.AccountAdd, "claude", "/u/picked", null, null)]
    [Arguments("remove", MutationVerb.AccountRemove, null, null, "a2", null)]
    [Arguments("rename", MutationVerb.AccountRename, null, null, "a2", "client")]
    [Arguments("rewire", MutationVerb.AccountRewire, null, null, "a2", null)]
    public Task Each_action_builds_its_request_with_the_verb_the_arguments_and_a_fresh_id(
            string action, MutationVerb verb, string? vendor, string? directory, string? accountId, string? label) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            using var rig = new Rig(Snapshot);
            rig.Picked = "/u/picked";
            var vm = rig.Vm;

            for (var i = 0; i < 2; i++) {
                switch (action) {
                    case "add candidate":
                        await vm.AddCandidateCommand.Execute(new AccountCandidate(HarnessId.Codex, "/u/.codex-alt", "home directory")).ToTask();
                        break;
                    case "add default":
                        await vm.AddDefaultCommand.Execute(rig.Row(".codex")).ToTask();
                        break;
                    case "add from picker":
                        await vm.AddFromPickerCommand.Execute("claude").ToTask();
                        break;
                    case "remove":
                        await vm.RemoveCommand.Execute(rig.Row("work")).ToTask();
                        break;
                    case "rename":
                        await vm.BeginRenameCommand.Execute(rig.Row("work")).ToTask();
                        vm.RenameDraft = " client ";
                        await vm.CommitRenameCommand.Execute().ToTask();
                        break;
                    case "rewire":
                        await vm.RewireCommand.Execute(rig.Row("work")).ToTask();
                        break;
                }
            }

            await Assert.That(rig.Requests.Count).IsEqualTo(2);
            foreach (var request in rig.Requests) {
                await Assert.That(request.Verb).IsEqualTo(verb);
                await Assert.That(request.Account).IsEqualTo(new AccountArgs(vendor, directory, accountId, label));
                await Assert.That(request.DaemonName).IsEqualTo("daemon-a");
                await Assert.That(request.Profile).IsEqualTo("work");
                await Assert.That(string.IsNullOrEmpty(request.RequestId)).IsFalse();
            }
            await Assert.That(rig.Requests[0].RequestId).IsNotEqualTo(rig.Requests[1].RequestId);
        });

    [Test]
    public Task Actions_run_one_at_a_time_and_report_the_outcome() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(Snapshot);
        var vm = rig.Vm;
        var pending = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Outcome = _ => pending.Task;
        var work = rig.Row("work");

        var removing = vm.RemoveCommand.Execute(work).ToTask();

        await Assert.That(vm.IsBusy).IsTrue();
        await Assert.That(CanRun(vm.RewireCommand, work)).IsFalse();
        await Assert.That(CanRun(vm.AddDefaultCommand, rig.Row(".codex"))).IsFalse();
        await Assert.That(CanRun(vm.BeginRenameCommand, work)).IsFalse();
        await vm.RewireCommand.Execute(work).ToTask();
        await Assert.That(rig.Requests.Count).IsEqualTo(1);

        pending.SetResult(new MutationOutcome.Succeeded());
        await removing;
        await Assert.That(vm.IsBusy).IsFalse();
        await Assert.That(vm.Notice).IsNull();

        rig.Outcome = _ => Task.FromResult<MutationOutcome>(new MutationOutcome.Failed(1, "plugin: hooks.json is read-only", RecoverySurface.Attention));
        await vm.RewireCommand.Execute(work).ToTask();
        await Assert.That(vm.Notice).IsEqualTo(new AccountsNotice("Could not re-wire work", "plugin: hooks.json is read-only"));
        await Assert.That(vm.Groups.Single(g => g.Vendor == "claude").Notice).IsEqualTo(vm.Notice);
        await Assert.That(vm.Groups.Single(g => g.Vendor == "codex").Notice).IsNull();

        rig.Outcome = _ => Task.FromResult<MutationOutcome>(new MutationOutcome.Unconfirmed("snapshot_not_seen"));
        await vm.AddDefaultCommand.Execute(rig.Row(".codex")).ToTask();
        await Assert.That(vm.Notice).IsEqualTo(new AccountsNotice("Adding .codex could not be confirmed", "The list shows the current state.", IsWarning: true));
        await Assert.That(vm.Groups.Single(g => g.Vendor == "claude").Notice).IsNull();
        await Assert.That(vm.Groups.Single(g => g.Vendor == "codex").Notice).IsEqualTo(vm.Notice);

        rig.Outcome = _ => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("cli_below_floor", RecoverySurface.Attention));
        await vm.AddDefaultCommand.Execute(rig.Row(".codex")).ToTask();
        await Assert.That(vm.Notice).IsEqualTo(new AccountsNotice("Could not add .codex", "Update kcap on this machine to manage accounts."));
        await Assert.That(rig.Requests.Count).IsEqualTo(4);
    });

    [Test]
    public async Task Refresh_outcome_notices() {
        await Assert.That(AccountsSettingsViewModel.OutcomeNotice(MutationVerb.AccountRefresh, "work", new MutationOutcome.Succeeded())).IsNull();
        await Assert.That(AccountsSettingsViewModel.OutcomeNotice(MutationVerb.AccountRefresh, "work", new MutationOutcome.Failed(2, null, RecoverySurface.Attention)))
            .IsEqualTo(new AccountsNotice("Could not refresh work", "kcap's command line stopped with an error (code 2)."));
    }

    [Test]
    public Task Refresh_runs_the_refresh_verb_for_the_row() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(Snapshot);

        await rig.Vm.RefreshCommand.Execute(rig.Row("work")).ToTask();

        await Assert.That(rig.Requests.Count).IsEqualTo(1);
        await Assert.That(rig.Requests[0].Verb).IsEqualTo(MutationVerb.AccountRefresh);
        await Assert.That(rig.Requests[0].Account!.AccountId).IsEqualTo("a2");
        await Assert.That(rig.Vm.Notice).IsNull();
    });

    static AccountsSnapshotDto UsageSnapshot(Func<AccountViewDto, AccountViewDto>? edit = null) {
        var work = Account("a2", "claude", "/u/.claude-work", "work") with {
            Email = "w@x.io", Plan = "Max", SignIn = SignInStates.SignedIn, LimitsError = "limits down",
            Reading = Reading(40, Now, Now.AddHours(2)),
        };
        var home = Account("a1", "claude", "/u/.claude", "home") with {
            SignIn = SignInStates.SignedOut, RefreshError = "refresh failed", Reading = Reading(95, Now, Now.AddMinutes(30)),
        };
        var main = Account("a3", "codex", "/u/.codex-main", "main", "installed") with { SignIn = SignInStates.SignedIn };
        return new("host-1", 3, [edit is null ? work : edit(work), home, main], [new("claude", "/u/.claude", "/u/.claude"), new("codex", "/u/.codex", "/u/.codex")]);
    }

    [Test]
    public Task Rows_carry_email_plan_and_error() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(UsageSnapshot());

        var work = rig.Row("work");
        await Assert.That(work.Email).IsEqualTo("w@x.io");
        await Assert.That(work.Plan).IsEqualTo("Max");
        await Assert.That(work.ErrorText).IsEqualTo("limits down");
        await Assert.That(rig.Row("home").ErrorText).IsEqualTo("refresh failed");
        await Assert.That(work.Usage.View).IsNotNull();
    });

    [Test]
    public Task A_snapshot_with_a_duplicate_account_id_keeps_the_first() => AvaloniaSession.RunOnUiAsync(async () => {
        var snapshot = UsageSnapshot();
        using var rig = new Rig(snapshot with { Accounts = [.. snapshot.Accounts!, snapshot.Accounts![0] with { Label = "dup", Email = "dup@x.io" }] });

        await Assert.That(rig.Row("work").Email).IsEqualTo("w@x.io");
    });

    [Test]
    public Task A_tick_updates_usage_without_rebuilding_groups() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(UsageSnapshot());
        var groups = rig.Vm.Groups;
        var slot = rig.Row("work").Usage;
        var before = slot.View;

        rig.Clock.OnNext(Now.AddMinutes(90));

        await Assert.That(rig.Vm.Groups).IsSameReferenceAs(groups);
        await Assert.That(rig.Row("work").Usage).IsSameReferenceAs(slot);
        await Assert.That(slot.View).IsNotEqualTo(before);
    });

    [Test]
    public Task A_resent_snapshot_keeps_the_same_slots() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(UsageSnapshot());
        var groups = rig.Vm.Groups;
        var slot = rig.Row("work").Usage;

        rig.Accounts.SnapshotSubject.OnNext(UsageSnapshot() with { Revision = 4 });

        await Assert.That(rig.Vm.Groups).IsSameReferenceAs(groups);
        await Assert.That(rig.Row("work").Usage).IsSameReferenceAs(slot);
    });

    [Test]
    public Task Cards_count_registered_accounts_only() => AvaloniaSession.RunOnUiAsync(async () => {
        var signedIn = Account("a2", "claude", "/u/.claude-work", "work") with { SignIn = SignInStates.SignedIn };
        using var rig = new Rig(new AccountsSnapshotDto("host-1", 1, [signedIn], [new("codex", "/u/.codex", "/u/.codex")]));

        await Assert.That(rig.Vm.Groups[1].Rows.Single().IsUnregisteredDefault).IsTrue();
        await Assert.That(rig.Vm.NeedsFixCard).IsEqualTo(new AccountSummaryCard("0", null, false));
        await Assert.That(rig.Vm.ReadyCard.Value).IsEqualTo("0 of 1");
    });

    [Test]
    public Task Ready_card_lists_ready_labels() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(UsageSnapshot());

        await Assert.That(rig.Vm.ReadyCard).IsEqualTo(new AccountSummaryCard("1 of 3", "work", false));
    });

    [Test]
    public Task Next_reset_card_names_account_and_window() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(UsageSnapshot());

        await Assert.That(rig.Vm.NextResetCard).IsEqualTo(new AccountSummaryCard("resets in 30m", "home · 5h", false));
    });

    [Test]
    public Task Next_reset_card_is_a_dash_when_nothing_resets() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(Snapshot);

        await Assert.That(rig.Vm.NextResetCard).IsEqualTo(new AccountSummaryCard("—", null, false));
    });

    [Test]
    public Task Need_a_fix_card_counts_signed_out_and_needs_trust() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(UsageSnapshot());

        await Assert.That(rig.Vm.NeedsFixCard).IsEqualTo(new AccountSummaryCard("2", "home · Needs sign-in", true));
    });

    [Test]
    public async Task Outcome_notices_name_the_action_and_never_read_an_unconfirmed_outcome_as_done() {
        await Assert.That(AccountsSettingsViewModel.OutcomeNotice(MutationVerb.AccountAdd, "work", new MutationOutcome.Succeeded())).IsNull();
        await Assert.That(AccountsSettingsViewModel.OutcomeNotice(MutationVerb.AccountRemove, "work", new MutationOutcome.Failed(2, null, RecoverySurface.Attention)))
            .IsEqualTo(new AccountsNotice("Could not remove work", "kcap's command line stopped with an error (code 2)."));
        await Assert.That(AccountsSettingsViewModel.OutcomeNotice(MutationVerb.AccountRemove, "work", new MutationOutcome.UnconfirmedNoAttach()))
            .IsEqualTo(new AccountsNotice("Removing work could not be confirmed", "The list shows the current state.", IsWarning: true));
    }

    [Test]
    [Arguments("cli_below_floor", "Update kcap on this machine to manage accounts.")]
    [Arguments("cli_version_unknown", "Update kcap on this machine to manage accounts.")]
    [Arguments("cli_override_missing", "Update kcap on this machine to manage accounts.")]
    [Arguments("cli_not_found", "Reinstall kcap to manage accounts.")]
    [Arguments("daemon_renamed_restart_app", "Restart the app to manage accounts.")]
    [Arguments("some_new_refusal", "some_new_refusal")]
    public async Task A_refusal_reads_as_what_to_do_and_an_unknown_one_keeps_its_reason(string reason, string expected) =>
        await Assert.That(AccountsSettingsViewModel.OutcomeNotice(
            MutationVerb.AccountAdd, "work", new MutationOutcome.Refused(reason, RecoverySurface.Attention)))
            .IsEqualTo(new AccountsNotice("Could not add work", expected));

    [Test]
    public Task Rename_needs_a_changed_non_blank_label() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(Snapshot);
        var vm = rig.Vm;
        var work = rig.Row("work");

        await vm.BeginRenameCommand.Execute(work).ToTask();
        await Assert.That(vm.Renaming).IsEqualTo(work);
        await Assert.That(vm.RenameDraft).IsEqualTo("work");
        await Assert.That(CanRun(vm.CommitRenameCommand, (object?)null)).IsFalse();

        vm.RenameDraft = "   ";
        await Assert.That(CanRun(vm.CommitRenameCommand, (object?)null)).IsFalse();
        vm.RenameDraft = " work ";
        await Assert.That(CanRun(vm.CommitRenameCommand, (object?)null)).IsFalse();
        await vm.CommitRenameCommand.Execute().ToTask();
        await Assert.That(rig.Requests.Count).IsEqualTo(0);

        vm.RenameDraft = "client";
        await Assert.That(CanRun(vm.CommitRenameCommand, (object?)null)).IsTrue();
        await vm.CancelRenameCommand.Execute().ToTask();
        await Assert.That(vm.Renaming).IsNull();
        await Assert.That(CanRun(vm.CommitRenameCommand, (object?)null)).IsFalse();

        await vm.BeginRenameCommand.Execute(rig.Row(".codex")).ToTask();
        await Assert.That(vm.Renaming).IsNull();

        await vm.BeginRenameCommand.Execute(work).ToTask();
        vm.RenameDraft = "client";
        await vm.CommitRenameCommand.Execute().ToTask();
        await Assert.That(vm.Renaming).IsNull();
        await Assert.That(rig.Requests.Single().Account!.Label).IsEqualTo("client");
        await Assert.That(vm.Notice).IsNull();
    });

    [Test]
    public Task RefreshCandidates_reads_discovery_each_time() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(Snapshot);
        var vm = rig.Vm;
        var first = new AccountCandidate(HarnessId.Claude, "/u/.claude-alt", "home directory");
        var second = new AccountCandidate(HarnessId.Codex, "/u/.codex-alt", "CODEX_HOME");

        rig.Found = [first];
        vm.RefreshCandidates();
        await WaitUntilAsync(() => vm.Candidates.Count == 1);
        await Assert.That(vm.Candidates.Single()).IsEqualTo(first);

        rig.Found = [first, second];
        vm.RefreshCandidates();
        await WaitUntilAsync(() => vm.Candidates.Count == 2);
        await Assert.That(vm.Candidates[1]).IsEqualTo(second);
        await Assert.That(rig.Discoveries).IsEqualTo(2);
        await Assert.That(rig.DiscoveredOnUiThread).IsFalse();
    });

    [Test]
    public Task A_listed_account_hides_only_its_own_vendor_in_a_shared_folder() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(Snapshot);
        var vm = rig.Vm;
        var codexBeside = new AccountCandidate(HarnessId.Codex, "/u/.claude-work", "home directory");

        rig.Found = [new AccountCandidate(HarnessId.Claude, "/u/.claude-work", "home directory"), codexBeside];
        vm.RefreshCandidates();
        await WaitUntilAsync(() => rig.Discoveries == 1 && vm.Candidates.Count > 0);

        await Assert.That(vm.Candidates.Single()).IsEqualTo(codexBeside);
    });

    [Test]
    public Task A_failure_after_the_daemon_drops_shows_without_a_card() => AvaloniaSession.RunOnUiAsync(async () => {
        using var rig = new Rig(Snapshot);
        var vm = rig.Vm;
        var pending = new TaskCompletionSource<MutationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Outcome = _ => pending.Task;

        var removing = vm.RemoveCommand.Execute(rig.Row("work")).ToTask();
        rig.Accounts.SnapshotSubject.OnNext(null);
        await WaitUntilAsync(() => vm.Groups.Count == 0);
        pending.SetResult(new MutationOutcome.Failed(1, "daemon went away", RecoverySurface.Attention));
        await removing;

        await Assert.That(vm.UnplacedNotice).IsEqualTo(new AccountsNotice("Could not remove work", "daemon went away"));
    });
}

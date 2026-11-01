using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Accounts.Readings;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// The launcher's account choice as HomeViewModel drives it from the server's listing, the
/// targeted host's snapshot and memory. Every test runs inside AvaloniaSession.WithImmediateRxScheduler
/// and carries [NotInParallel("AvaloniaSession")], like HomeViewModelTests.
public class HomeViewModelAccountTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    const string Viewer = "u1";
    const string LocalName = "daemon-a";
    const string Repo = "/repo/a";
    const string Loading = "Loading accounts…";

    static readonly AccountViewDto Home = Account("a1", "home", "/u/.claude");
    static readonly AccountViewDto Work = Account("a2", "work", "/u/.claude-work");
    static readonly AccountViewDto Side = Account("a3", "side", "/u/.claude-side");
    static readonly AccountViewDto CodexWork = Account("c2", "codex-work", "/u/.codex-work", "codex");

    static AccountViewDto Account(string id, string label, string directory, string vendor = "claude") =>
        new(id, vendor, directory, directory, label, "recording");

    static AccountsSnapshotDto Snapshot(string hostId, long revision, params AccountViewDto[] accounts) =>
        new(hostId, revision, accounts, [new("claude", "/u/.claude", "/u/.claude"), new("codex", "/u/.codex", "/u/.codex")]);

    static DaemonInfo Listed(string name, bool accounts = true, string owner = Viewer, string? machineId = null, string[]? repos = null) => new() {
        Name = name, OwnerUserId = owner, MachineId = machineId, Connected = true, SupportsAccountLaunches = accounts,
        RepoPaths = repos, SupportedVendors = ["claude", "codex", "pi"],
    };

    static DaemonListing Answered(int generation, params DaemonInfo[] daemons) => new(daemons, generation, false);

    static DaemonAccountsDto AccountsOf(string daemonName, string hostId, params AccountViewDto[] accounts) => new() {
        DaemonName = daemonName, HostId = hostId, Revision = 1,
        Accounts = [.. accounts.Select(a => new ListedAccountDto {
            Id = a.Id, Vendor = a.Vendor, Directory = a.Directory, DirectoryKey = a.DirectoryKey, Label = a.Label,
            RecordingState = a.RecordingState,
        })],
        Defaults = [
            new() { Vendor = "claude", Directory = "/u/.claude", DirectoryKey = "/u/.claude" },
            new() { Vendor = "codex", Directory = "/u/.codex", DirectoryKey = "/u/.codex" },
        ],
    };

    static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    /// A five-hour window at 42% that resets in 2h30m30s, confirmed at Now.
    static readonly AccountReadingDto FiveHour = new(
        0, [new UsageWindowDto("claude", 300, 42, Now.AddMinutes(150).AddSeconds(30), Now)], null, null, null);

    static AccountViewDto WithReading(AccountViewDto account) => account with { Reading = FiveHour };

    static DaemonAccountsDto RemoteWithReading(string daemonName, string hostId, string accountId) {
        var row = AccountsOf(daemonName, hostId, Account(accountId, accountId, "/u/." + accountId));
        row.Accounts![0] = row.Accounts[0] with {
            Reading = new ListedReadingDto {
                Windows = [new ListedUsageWindowDto {
                    LimitId = "claude", WindowMinutes = 300, UsedPercent = 42, ResetsAt = Now.AddMinutes(150).AddSeconds(30), ConfirmedAt = Now,
                }],
            },
        };
        return row;
    }

    static string Refusal(string label) => $"{label} is not available on this machine. Use the default account or pick another.";

    sealed class RecordingLaunchClient : ILaunchClient {
        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<string>?>(System.Collections.Frozen.FrozenSet<string>.Empty);
        public List<LaunchRequest> Requests { get; } = [];

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Task.FromResult(new LaunchOutcome(true, LaunchedId, null));
        }
    }

    /// A connected local daemon and server lane, the viewer's id, and each account input as a
    /// subject the test drives. Memory is written before the view-model loads it.
    sealed class Rig(string statePath) : IDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeRemoteAgents Remote { get; } = new();
        public BehaviorSubject<AccountsSnapshotDto?> LocalAccounts { get; } = new(null);
        public BehaviorSubject<ServerLaneStatus> Lane { get; } = new(new ServerLaneStatus(ServerLaneState.Connected));
        public RecordingLaunchClient Launch { get; } = new();
        public AppStateStore Store { get; } = new(statePath);
        public BehaviorSubject<DateTimeOffset> Clock { get; } = new(Now);
        public Func<Task> OnRetry { get; set; } = () => Task.CompletedTask;
        public HomeViewModel Vm { get; private set; } = null!;

        public async Task<HomeViewModel> StartAsync(
                (string Host, string Vendor, string Repo, string Id)[]? memory = null, IRemoteAgentsService? remote = null,
                DaemonStatusDto? local = null, string? localMachineId = null, Func<CancellationToken, Task<string?>>? viewerId = null) {
            if (memory is not null)
                await Store.UpdateAsync(s => s with {
                    AccountByRepo = memory.ToDictionary(m => AccountMemory.KeyFor(m.Host, m.Vendor, m.Repo), m => m.Id),
                });
            Daemon.SnapshotsSubject.OnNext(local ?? FakeDaemonClientService.Snap());
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
            var listings = remote ?? Remote;
            Vm = new HomeViewModel(
                Daemon, Store, Launch, () => Task.FromResult<string[]>([]), TimeProvider.System,
                daemons: Remote.Daemons, viewerId: viewerId ?? (_ => Task.FromResult<string?>(Viewer)), laneStatus: Lane,
                localMachineId: localMachineId,
                listing: listings.Listing, daemonAccounts: listings.DaemonAccounts, localAccounts: LocalAccounts,
                retryListing: () => OnRetry(),
                readings: new AccountReadings(LocalAccounts, Remote.DrawableDaemonAccounts, Clock));
            await Vm.AccountMemoryLoadedForTesting;
            return Vm;
        }

        /// The local target with its listing answered under the flag, the repository chosen and
        /// the given snapshot at hand.
        public async Task<HomeViewModel> LocalAsync(
                AccountsSnapshotDto? snapshot, (string Host, string Vendor, string Repo, string Id)[]? memory = null) {
            var vm = await StartAsync(memory);
            Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName)));
            await vm.SelectRepositoryAsync(Repo);
            LocalAccounts.OnNext(snapshot);
            return vm;
        }

        public void Dispose() {
            Vm?.Dispose();
            LocalAccounts.Dispose();
            Clock.Dispose();
            Lane.Dispose();
            Remote.Dispose();
        }
    }

    static async Task<string?> Stored(AppStateStore store, string host, string vendor, string repo) =>
        AccountMemory.Lookup((await store.LoadAsync()).AccountByRepo, host, vendor, repo);

    static async Task Eventually(Func<Task<bool>> condition) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("the condition was never reached");
            await Task.Delay(10);
        }
    }

    static Task Eventually(Func<bool> condition) => Eventually(() => Task.FromResult(condition()));

    static string SelectedRows(HomeViewModel vm) =>
        string.Join(",", vm.AccountRows.Where(c => c.Selected).Select(c => (c.Removed ? "removed:" : "") + (c.Row.Id ?? "default")));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_section_shows_at_two_rows_and_not_at_one_counting_an_unregistered_default() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, Work));

            await Assert.That(vm.AccountSection).IsTrue();
            await Assert.That(vm.AccountRows.Select(c => c.Row.Id ?? "default")).IsEquivalentTo(new[] { "a2", "default" });
            await Assert.That(vm.AccountRows.Single(c => c.Row.Id is null).Row.IsDefault).IsTrue();
            await Assert.That(SelectedRows(vm)).IsEqualTo("default");

            rig.LocalAccounts.OnNext(Snapshot("h1", 2, Home));
            await Assert.That(vm.AccountRows.Count).IsEqualTo(1);
            await Assert.That(vm.AccountSection).IsFalse();

            rig.LocalAccounts.OnNext(Snapshot("h1", 3));
            await Assert.That(vm.AccountRows.Count).IsEqualTo(1);
            await Assert.That(vm.AccountSection).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_section_is_absent_without_a_complete_snapshot_and_for_a_daemon_listed_without_the_flag() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(snapshot: null);

            // Waiting on the first snapshot shows the loading line, never rows.
            await Assert.That(vm.AccountRows).IsEmpty();
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);

            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            await Assert.That(vm.AccountSection).IsTrue();
            await vm.PickAccountAsync("a2");

            rig.LocalAccounts.OnNext(null);
            await Assert.That(vm.AccountSection).IsFalse();
            await Assert.That(vm.AccountRows).IsEmpty();

            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName, accounts: false)));
            await vm.SelectRepositoryAsync("/repo/b");
            await Assert.That(vm.AccountSection).IsFalse();
            await Assert.That(vm.AccountPendingText).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_empty_registry_on_a_supported_target_resolves_to_the_default_and_enables_Start() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 0));

            await Assert.That(vm.AccountSection).IsFalse();
            await Assert.That(vm.AccountPendingText).IsNull();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");

            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_vendor_without_accounts_never_gates_Start_and_sends_null() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync();
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work, Account("p1", "pi-work", "/u/.pi", "pi")));

            // The listing never answered, so Claude waits; Pi has no accounts to wait on.
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            await vm.ChooseHarnessAsync("pi");
            await vm.PickAccountAsync("p1");

            await Assert.That(vm.AccountSection).IsFalse();
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");

            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().Vendor).IsEqualTo("pi");
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chip_label_carries_the_account_only_when_it_is_not_the_default() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, Home, Work));

            await Assert.That(vm.SelectedAccountLabel).IsNull();

            await vm.PickAccountAsync("a2");
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");

            // The Default-badged row follows the daemon's default rather than pinning its account.
            await vm.PickAccountAsync("a1");
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(SelectedRows(vm)).IsEqualTo("a1");
            await Assert.That(await Stored(rig.Store, "h1", "claude", Repo)).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_request_carries_null_for_the_default_and_the_id_for_an_explicit_pick() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, Home, Work));

            vm.Goal = "one";
            await vm.StartCommand.Execute();

            await vm.PickAccountAsync("a2");
            vm.Goal = "two";
            await vm.StartCommand.Execute();

            await vm.PickAccountAsync("a1");
            vm.Goal = "three";
            await vm.StartCommand.Execute();

            await Assert.That(rig.Launch.Requests.Select(r => r.AccountId ?? "null")).IsEquivalentTo(
                new[] { "null", "a2", "null" }, CollectionOrdering.Matching);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Restore_runs_on_a_repository_vendor_or_machine_change_once_the_snapshot_is_complete() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var box = Listed("box", repos: ["/w/repo"]);
            rig.Remote.DaemonsSubject.OnNext([box]);
            var vm = await rig.StartAsync(memory: [
                ("h1", "claude", Repo, "a2"), ("h1", "claude", "/repo/b", "a3"), ("h1", "codex", "/repo/b", "c2"),
                ("h2", "codex", "/w/repo", "d2"),
            ]);
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName), box));
            await vm.SelectRepositoryAsync(Repo);

            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);

            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work, Side, CodexWork));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");

            await vm.SelectRepositoryAsync("/repo/b");
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("side");

            await vm.ChooseHarnessAsync("codex");
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("codex-work");

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.SelectedVendor).IsEqualTo("codex");
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            rig.Remote.DaemonAccountsSubject.OnNext([
                AccountsOf("box", "h2", Account("d1", "box-home", "/u/.codex", "codex"), Account("d2", "box-work", "/u/.codex-work", "codex")),
            ]);
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("box-work");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().DaemonName).IsEqualTo("box");
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsEqualTo("d2");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_snapshot_restores_unless_the_person_has_picked_since() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(snapshot: null, memory: [("h1", "claude", Repo, "a2")]);

            await Assert.That(vm.SelectedAccountLabel).IsNull();
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work, Side));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");

            rig.LocalAccounts.OnNext(null);
            await vm.PickAccountAsync("a3");
            rig.LocalAccounts.OnNext(Snapshot("h1", 2, Home, Work, Side));

            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("side");
            await Assert.That(SelectedRows(vm)).IsEqualTo("a3");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_entry_absent_from_its_hosts_snapshot_is_dropped_and_one_survives_a_missing_snapshot_a_disconnect_and_a_switch_to_another_host() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var box = Listed("box", repos: [Repo]);
            rig.Remote.DaemonsSubject.OnNext([box]);
            var vm = await rig.StartAsync(memory: [("h1", "claude", Repo, "a2")]);
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName), box));
            rig.Remote.DaemonAccountsSubject.OnNext([AccountsOf("box", "h2", Account("b1", "box-home", "/u/.claude"))]);
            await vm.SelectRepositoryAsync(Repo);
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);

            await vm.SelectMachineAsync("box", isLocal: false);
            await Assert.That(vm.SelectedRepoPath).IsEqualTo(Repo);
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(vm.AccountPendingText).IsNull();

            await vm.SelectMachineAsync(LocalName, isLocal: true);
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");

            // A disconnect loses the snapshot and the listing alike.
            rig.LocalAccounts.OnNext(null);
            rig.Remote.ListingSubject.OnNext(new DaemonListing(null, 2, false));
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            rig.Remote.ListingSubject.OnNext(Answered(2, Listed(LocalName), box));
            rig.LocalAccounts.OnNext(Snapshot("h1", 2, Home, Work));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");
            await Assert.That(await Stored(rig.Store, "h1", "claude", Repo)).IsEqualTo("a2");

            rig.LocalAccounts.OnNext(Snapshot("h1", 3, Home));
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(SelectedRows(vm)).IsEqualTo("a1");

            rig.LocalAccounts.OnNext(Snapshot("h1", 4, Home, Work));
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Eventually(async () => await Stored(rig.Store, "h1", "claude", Repo) is null);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_default_pick_removes_the_entry_and_a_vendor_switch_restores_the_other_vendors_entry() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(
                Snapshot("h1", 1, Home, Work, CodexWork), memory: [("h1", "claude", Repo, "a2"), ("h1", "codex", Repo, "c2")]);
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");

            await vm.PickAccountAsync("a1");
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(await Stored(rig.Store, "h1", "claude", Repo)).IsNull();
            await Assert.That(await Stored(rig.Store, "h1", "codex", Repo)).IsEqualTo("c2");

            await vm.ChooseHarnessAsync("codex");
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("codex-work");

            await vm.ChooseHarnessAsync("claude");
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(SelectedRows(vm)).IsEqualTo("a1");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Two_daemons_on_one_host_share_one_memory() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var first = Listed("box-1", repos: ["/w/repo"]);
            var second = Listed("box-2", repos: ["/w/repo"]);
            rig.Remote.DaemonsSubject.OnNext([first, second]);
            var vm = await rig.StartAsync();
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName), first, second));
            var accounts = new[] { Account("w1", "shared-home", "/u/.claude"), Account("w2", "shared-work", "/u/.claude-work") };
            rig.Remote.DaemonAccountsSubject.OnNext([AccountsOf("box-1", "h9", accounts), AccountsOf("box-2", "h9", accounts)]);

            await vm.SelectMachineAsync("box-1", isLocal: false);
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await vm.PickAccountAsync("w2");

            await vm.SelectMachineAsync("box-2", isLocal: false);
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("shared-work");
            await Assert.That((await rig.Store.LoadAsync()).AccountByRepo!.Keys).IsEquivalentTo(
                new[] { AccountMemory.KeyFor("h9", "claude", "/w/repo") });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remembered_local_account_behind_an_old_server_restores_nothing_and_sends_null_only_once_the_listing_answered() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(memory: [("h1", "claude", Repo, "a2")]);
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            vm.Goal = "g";

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests).IsEmpty();

            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName, accounts: false)));

            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(vm.AccountSection).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
            await Assert.That(await Stored(rig.Store, "h1", "claude", Repo)).IsEqualTo("a2");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_ready_local_daemon_with_a_delayed_or_failed_first_listing_captures_no_draft_until_the_listing_and_snapshot_resolve() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(memory: [("h1", "claude", Repo, "a2")]);
            await vm.SelectRepositoryAsync(Repo);
            vm.Goal = "g";

            await Assert.That(vm.ConnectionNotice).IsNull();
            await Assert.That(vm.StartButtonTip).IsEqualTo(Loading);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests).IsEmpty();
            await Assert.That(vm.Goal).IsEqualTo("g");
            await Assert.That(vm.AccountRetryVisible).IsFalse();

            rig.Remote.ListingSubject.OnNext(new DaemonListing(null, 1, true));
            await Assert.That(vm.AccountRetryVisible).IsTrue();
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);

            var retries = 0;
            rig.OnRetry = () => {
                retries++;
                rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName)));
                return Task.CompletedTask;
            };
            await vm.RetryAccountsCommand.Execute();

            await Assert.That(retries).IsEqualTo(1);
            await Assert.That(vm.AccountRetryVisible).IsFalse();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");

            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsEqualTo("a2");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_listing_that_answered_without_the_local_daemon_leaves_support_unknown_and_Start_waiting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync();
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));

            // A same-named daemon of another owner is not this one.
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed("box"), Listed(LocalName, owner: "u2")));

            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(vm.AccountRetryVisible).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo(Loading);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            rig.Remote.ListingSubject.OnNext(Answered(1, Listed("box"), Listed(LocalName)));
            await Assert.That(vm.AccountPendingText).IsNull();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reconnect_returns_support_to_unknown_and_Start_waits() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, Home, Work));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            rig.Remote.ListingSubject.OnNext(new DaemonListing(null, 2, false));
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            rig.Remote.ListingSubject.OnNext(Answered(2, Listed(LocalName)));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        });
    }

    /// The listing guard lives in RemoteAgentsService; this observes it through the launcher: a
    /// supported answer to a fetch from before the reconnect never restores the remembered account.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_result_from_a_superseded_connection_is_ignored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var calls = 0;
            var stale = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>();
            var fresh = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>();
            var lane = new FakeServerLane { DaemonsHandler = () => Interlocked.Increment(ref calls) == 1 ? stale.Task : fresh.Task };
            using var service = new RemoteAgentsService(
                lane, _ => Task.FromResult(new RemoteFetch([])), TimeSpan.Zero, time: new FakeTimeProvider());
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(memory: [("h1", "claude", Repo, "a2")], remote: service);
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));

            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: Viewer));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: Viewer));
            stale.SetResult([Listed(LocalName)]);
            await Eventually(() => Volatile.Read(ref calls) == 2);

            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            fresh.SetResult([Listed(LocalName, accounts: false)]);
            await Eventually(() => vm.AccountPendingText is null);

            await Assert.That(vm.SelectedAccountLabel).IsNull();
            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_choice_made_on_a_supported_target_survives_support_withdrawal_as_explicit_invalid_and_Start_refuses_named() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, Home, Work));
            await vm.PickAccountAsync("a2");
            vm.Goal = "g";

            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName, accounts: false)));

            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work — unavailable");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(vm.StartButtonTip).IsEqualTo("Start");
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests).IsEmpty();
            await Assert.That(vm.StartError).IsEqualTo(Refusal("work"));
            await Assert.That(vm.StartErrorOffersDefault).IsTrue();

            // Picking a row answers the refusal, even a row that is still unavailable.
            await vm.PickAccountAsync("a2");
            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.StartErrorOffersDefault).IsFalse();
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work — unavailable");

            rig.Remote.ListingSubject.OnNext(new DaemonListing(null, 2, false));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work — unavailable");
            await Assert.That(vm.AccountPendingText).IsNull();

            rig.Remote.ListingSubject.OnNext(Answered(2, Listed(LocalName)));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsEqualTo("a2");
            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.StartErrorOffersDefault).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Start_before_the_first_complete_snapshot_of_a_supported_target_is_disabled_and_captures_no_draft() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(snapshot: null);
            vm.Goal = "g";

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.StartButtonTip).IsEqualTo(Loading);
            await vm.StartCommand.Execute();

            await Assert.That(rig.Launch.Requests).IsEmpty();
            await Assert.That(vm.Goal).IsEqualTo("g");
            await Assert.That(vm.StartError).IsNull();

            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Removing_the_only_non_default_account_keeps_the_section_and_Use_default_lets_the_next_Start_succeed() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, Work));
            await vm.PickAccountAsync("a2");
            vm.Goal = "g";

            rig.LocalAccounts.OnNext(Snapshot("h1", 2));

            await Assert.That(vm.AccountSection).IsTrue();
            await Assert.That(SelectedRows(vm)).IsEqualTo("removed:a2");
            await Assert.That(vm.AccountRows.Any(c => c.Row is { Id: null, IsDefault: true })).IsTrue();

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests).IsEmpty();
            await Assert.That(vm.StartError).IsEqualTo(Refusal("work"));
            await Assert.That(vm.StartErrorOffersDefault).IsTrue();

            await vm.UseDefaultAccountCommand.Execute();
            await Assert.That(vm.StartError).IsNull();
            await Assert.That(vm.StartErrorOffersDefault).IsFalse();
            await Assert.That(vm.SelectedAccountLabel).IsNull();
            await Assert.That(vm.AccountSection).IsFalse();
            await Assert.That(rig.Launch.Requests).IsEmpty();

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
        });
    }

    /// A server without sign-in gives no viewer id: the local daemon's row is the one carrying its
    /// name, and two rows with that name leave support unknown.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_viewer_id_the_local_target_matches_the_only_row_with_its_name() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(viewerId: _ => Task.FromResult<string?>(null));
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));

            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName, owner: "o1"), Listed(LocalName, owner: "o2")));
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName, owner: "o1")));
            await Assert.That(vm.AccountPendingText).IsNull();
            await Assert.That(vm.AccountSection).IsTrue();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            await vm.PickAccountAsync("a2");
            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsEqualTo("a2");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_listing_answered_before_the_viewer_id_resolved_is_read_again_once_it_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var viewer = new TaskCompletionSource<string?>();
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(viewerId: _ => viewer.Task);
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName)));

            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            viewer.SetResult(Viewer);
            await Eventually(() => vm.AccountPendingText is null);

            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Model_rows_follow_the_selected_accounts_catalog_then_the_machines_then_the_curated_list() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(local: FakeDaemonClientService.Snap(
                vendorModels: new(StringComparer.Ordinal) { ["claude"] = [new("machine-model", "Machine")] },
                vendorModelsByAccount: new(StringComparer.Ordinal) { ["a2"] = [new("work-model", "Work")] }));
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName)));
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work, Side));

            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).IsEquivalentTo(new[] { "machine-model" });

            await vm.PickAccountAsync("a2");
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).IsEquivalentTo(new[] { "work-model" });
            vm.SelectedModel = "work-model";

            await vm.PickAccountAsync("a3");
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).IsEquivalentTo(new[] { "machine-model" });
            await Assert.That(vm.SelectedModel).IsEqualTo("");

            rig.Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(
                vendorModelsByAccount: new(StringComparer.Ordinal) { ["a2"] = [new("work-model", "Work")] }));
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).Contains("claude-opus-5");
        });
    }

    /// A remote host's accounts arrive only through the server's fetch, so a supported target
    /// without its row offers Retry rather than waiting on a push that may never come.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_supported_remote_daemon_without_an_accounts_row_offers_Retry_and_resolves_once_the_row_arrives() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var box = Listed("box", repos: ["/w/repo"]);
            rig.Remote.DaemonsSubject.OnNext([box]);
            var vm = await rig.StartAsync();
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName), box));
            await vm.SelectMachineAsync("box", isLocal: false);
            vm.Goal = "g";

            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(vm.AccountRetryVisible).IsTrue();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            var retries = 0;
            rig.OnRetry = () => {
                retries++;
                rig.Remote.DaemonAccountsSubject.OnNext([
                    AccountsOf("box", "h2", Account("b1", "box-home", "/u/.claude"), Account("b2", "box-work", "/u/.claude-work")),
                ]);
                return Task.CompletedTask;
            };
            await vm.RetryAccountsCommand.Execute();

            await Assert.That(retries).IsEqualTo(1);
            await Assert.That(vm.AccountPendingText).IsNull();
            await Assert.That(vm.AccountRetryVisible).IsFalse();
            await Assert.That(vm.AccountSection).IsTrue();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().DaemonName).IsEqualTo("box");
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsNull();
        });
    }

    /// The hub lists a daemon still registered under a claimed legacy id for its viewer, so the local
    /// daemon's row is the one carrying its machine id and name, under whatever owner it is listed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_local_daemon_is_its_twin_row_whatever_owner_the_listing_carries() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(localMachineId: "m1");
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));

            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName, owner: "legacy", machineId: "m2")));
            await Assert.That(vm.AccountPendingText).IsEqualTo(Loading);
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();

            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName, owner: "legacy", machineId: "m1")));
            await Assert.That(vm.AccountPendingText).IsNull();
            await Assert.That(vm.AccountSection).IsTrue();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();

            await vm.PickAccountAsync("a2");
            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsEqualTo("a2");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_model_from_the_picked_accounts_catalog_survives_support_going_unknown_and_back() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.StartAsync(local: FakeDaemonClientService.Snap(
                vendorModels: new(StringComparer.Ordinal) { ["claude"] = [new("machine-model", "Machine")] },
                vendorModelsByAccount: new(StringComparer.Ordinal) { ["a2"] = [new("work-model", "Work")] }));
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName)));
            await vm.SelectRepositoryAsync(Repo);
            rig.LocalAccounts.OnNext(Snapshot("h1", 1, Home, Work));
            await vm.PickAccountAsync("a2");
            vm.SelectedModel = "work-model";

            rig.Remote.ListingSubject.OnNext(new DaemonListing(null, 2, false));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work — unavailable");
            await Assert.That(vm.SelectedModel).IsEqualTo("work-model");

            rig.Remote.ListingSubject.OnNext(Answered(2, Listed(LocalName)));
            await Assert.That(vm.SelectedAccountLabel).IsEqualTo("work");
            await Assert.That(vm.SelectedModel).IsEqualTo("work-model");
            await Assert.That(vm.ModelChoicesFor("claude").Select(m => m.Slug)).IsEquivalentTo(new[] { "work-model" });

            vm.Goal = "g";
            await vm.StartCommand.Execute();
            await Assert.That(rig.Launch.Requests.Single().AccountId).IsEqualTo("a2");
            await Assert.That(rig.Launch.Requests.Single().Model).IsEqualTo("work-model");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Usage_is_projected_for_the_selected_hosts_accounts() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, WithReading(Home), Work, CodexWork));

            await Assert.That(vm.AccountUsageById.Keys).IsEquivalentTo(new[] { "a1", "a2" });
            var bar = vm.AccountUsageById["a1"].Bars.Single();
            await Assert.That(bar.Label).IsEqualTo("5h");
            await Assert.That(bar.PercentText).IsEqualTo("42%");
            await Assert.That(vm.AccountUsageById["a2"].LineText).IsEqualTo("no reading yet");

            await vm.ChooseHarnessAsync("codex");
            await Assert.That(vm.AccountUsageById.Keys).IsEquivalentTo(new[] { "c2" });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_snapshot_with_a_duplicate_account_id_keeps_the_first() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, WithReading(Home), Home with { Label = "dup", Reading = null }));

            await Assert.That(vm.AccountUsageById.Keys).IsEquivalentTo(new[] { "a1" });
            await Assert.That(vm.AccountUsageById["a1"].Bars.Single().PercentText).IsEqualTo("42%");
        });
    }

    [Test]
    public async Task The_launcher_comparison_ignores_age_and_readiness() {
        var a = new AccountUsageView([], "line", null, null, null, "updated 1m ago", false);

        await Assert.That(AccountUsageView.DrawnUsageEquals(new Dictionary<string, AccountUsageView> { ["a1"] = a },
            new Dictionary<string, AccountUsageView> { ["a1"] = a with { AgeText = "updated 2m ago", Ready = true } })).IsTrue();
        await Assert.That(AccountUsageView.DrawnUsageEquals(new Dictionary<string, AccountUsageView> { ["a1"] = a },
            new Dictionary<string, AccountUsageView> { ["a1"] = a with { BlockedText = "Limit hit" } })).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_tick_that_changes_no_text_publishes_nothing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, WithReading(Home), Work));
            var published = 0;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HomeViewModel.AccountUsageById)) published++; };

            rig.Clock.OnNext(Now.AddSeconds(10));

            await Assert.That(published).IsEqualTo(0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_tick_crossing_a_reset_updates_the_usage() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, WithReading(Home), Work));
            var before = vm.AccountUsageById["a1"];

            rig.Clock.OnNext(Now.AddHours(3));

            await Assert.That(vm.AccountUsageById["a1"]).IsNotEqualTo(before);
            await Assert.That(vm.AccountUsageById["a1"].Bars.Single().Percent).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Usage_empties_when_the_remote_gate_closes_while_choice_rows_stay() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var box = Listed("box", repos: ["/w/repo"]);
            rig.Remote.DaemonsSubject.OnNext([box]);
            var vm = await rig.StartAsync();
            rig.Remote.ListingSubject.OnNext(Answered(1, Listed(LocalName), box));
            await vm.SelectRepositoryAsync("/w/repo");
            await vm.SelectMachineAsync("box", isLocal: false);
            var row = RemoteWithReading("box", "h2", "d1");
            row = row with { Accounts = [.. row.Accounts!, row.Accounts![0] with { Id = "d2", Label = "d2", Directory = "/u/.d2", DirectoryKey = "/u/.d2", Reading = null }] };
            rig.Remote.DaemonAccountsSubject.OnNext([row]);
            rig.Remote.DrawableDaemonAccountsSubject.OnNext([row]);

            await Assert.That(vm.AccountUsageById["d1"].Bars.Single().PercentText).IsEqualTo("42%");

            rig.Remote.DrawableDaemonAccountsSubject.OnNext([]);

            await Assert.That(vm.AccountUsageById).IsEmpty();
            await Assert.That(vm.AccountRows.Select(c => c.Row.Id)).Contains("d1");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_usage_empties_on_detach() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig(Tmp.PathTo("app-state.json"));
            var vm = await rig.LocalAsync(Snapshot("h1", 1, WithReading(Home), Work));
            await Assert.That(vm.AccountUsageById).IsNotEmpty();

            rig.LocalAccounts.OnNext(null);

            await Assert.That(vm.AccountUsageById).IsEmpty();
        });
    }
}

using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Only a confirmed outcome reads as done: the next snapshot shows what an unconfirmed one left behind.
public sealed class AccountsSettingsViewModel : ReactiveObject, IDisposable {
    static readonly HarnessId[] Vendors = [HarnessId.Claude, HarnessId.Codex];
    const string UpdateKcap = "Update kcap on this machine to manage accounts";

    readonly Func<MutationRequest, CancellationToken, Task<MutationOutcome>> _runMutation;
    readonly string _daemonName;
    readonly string? _profileName;
    readonly Func<IReadOnlyList<AccountCandidate>> _discover;
    readonly Func<CancellationToken, Task<string?>> _pickDirectory;
    readonly CancellationTokenSource _lifetime;
    readonly CancellationToken _token;
    readonly CompositeDisposable _subscriptions = new();
    AccountsSnapshotDto? _snapshot;
    bool _supported;
    bool _lacksCapability;
    bool _canManage;
    IReadOnlyList<AccountVendorGroup> _groups = [];
    IReadOnlyList<AccountCandidate> _candidates = [];
    AccountsNotice? _notice;
    string? _noticeVendor;
    bool _isBusy;
    AccountSettingsRow? _renaming;
    string _renameDraft = "";
    int _candidateReads;
    readonly Dictionary<string, AccountUsageSlot> _slots = new(StringComparer.Ordinal);
    readonly AccountUsageSlot _defaultSlot = new();
    readonly TimeZoneInfo _zone = TimeZoneInfo.Local;
    DateTimeOffset? _now;
    AccountSummaryCard _readyCard = new("0 of 0", null, false);
    AccountSummaryCard _nextResetCard = new("—", null, false);
    AccountSummaryCard _needsFixCard = new("0", null, false);

    public AccountsSettingsViewModel(
            IAccountsService accounts, Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
            string daemonName, string? profileName, Func<IReadOnlyList<AccountCandidate>> discover,
            Func<CancellationToken, Task<string?>> pickDirectory, IObservable<AttachStatus> daemonStatus,
            IObservable<DateTimeOffset>? usageClock = null, CancellationToken appLifetime = default) {
        _runMutation = runMutation;
        _daemonName = daemonName;
        _profileName = profileName;
        _discover = discover;
        _pickDirectory = pickDirectory;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime);
        _token = _lifetime.Token;

        var idle = this.WhenAnyValue(x => x.CanManage, x => x.IsBusy, (manage, busy) => manage && !busy);
        AddCandidateCommand = ReactiveCommand.CreateFromTask<AccountCandidate>(AddCandidateAsync, idle).DisposeWith(_subscriptions);
        AddFromPickerCommand = ReactiveCommand.CreateFromTask<string>(AddFromPickerAsync, idle).DisposeWith(_subscriptions);
        AddDefaultCommand = ReactiveCommand.CreateFromTask<AccountSettingsRow>(AddDefaultAsync, idle).DisposeWith(_subscriptions);
        RefreshCommand = ReactiveCommand.CreateFromTask<AccountSettingsRow>(RefreshAsync, idle).DisposeWith(_subscriptions);
        RewireCommand = ReactiveCommand.CreateFromTask<AccountSettingsRow>(RewireAsync, idle).DisposeWith(_subscriptions);
        RemoveCommand = ReactiveCommand.CreateFromTask<AccountSettingsRow>(RemoveAsync, idle).DisposeWith(_subscriptions);
        BeginRenameCommand = ReactiveCommand.Create<AccountSettingsRow>(BeginRename, idle).DisposeWith(_subscriptions);
        CancelRenameCommand = ReactiveCommand.Create(() => { Renaming = null; }, idle).DisposeWith(_subscriptions);
        var renamable = this.WhenAnyValue(x => x.CanManage, x => x.IsBusy, x => x.Renaming, x => x.RenameDraft,
            (manage, busy, row, draft) => manage && !busy && RenameChanges(row, draft));
        CommitRenameCommand = ReactiveCommand.CreateFromTask(CommitRenameAsync, renamable).DisposeWith(_subscriptions);

        // The notice reads the capability off the status itself: Supported arrives on a stream of its
        // own, and on a disconnect it can land a dispatcher job before the status does.
        daemonStatus.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(status => {
            _lacksCapability = status.State == AttachState.Connected && status.Capabilities?.Contains(AccountsService.Capability) != true;
            Refresh();
        }).DisposeWith(_subscriptions);
        accounts.Supported.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(supported => {
            _supported = supported;
            Refresh();
        }).DisposeWith(_subscriptions);
        accounts.Snapshot.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(snapshot => {
            _snapshot = snapshot;
            ApplyGroups(GroupsFor(snapshot));
            UpdateUsage();
            Refresh();
        }).DisposeWith(_subscriptions);
        usageClock?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(now => {
            _now = now;
            UpdateUsage();
        }).DisposeWith(_subscriptions);
    }

    public IReadOnlyList<AccountVendorGroup> Groups {
        get => _groups;
        private set {
            this.RaiseAndSetIfChanged(ref _groups, value);
            this.RaisePropertyChanged(nameof(UnplacedNotice));
        }
    }

    public AccountSummaryCard ReadyCard {
        get => _readyCard;
        private set => this.RaiseAndSetIfChanged(ref _readyCard, value);
    }

    public AccountSummaryCard NextResetCard {
        get => _nextResetCard;
        private set => this.RaiseAndSetIfChanged(ref _nextResetCard, value);
    }

    public AccountSummaryCard NeedsFixCard {
        get => _needsFixCard;
        private set => this.RaiseAndSetIfChanged(ref _needsFixCard, value);
    }

    public bool CanManage {
        get => _canManage;
        private set => this.RaiseAndSetIfChanged(ref _canManage, value);
    }

    public string? ReadOnlyNotice =>
        _lacksCapability ? UpdateKcap
        : _snapshot is null ? "Accounts are listed once the daemon is connected"
        : null;

    public AccountsNotice? Notice {
        get => _notice;
        private set {
            this.RaiseAndSetIfChanged(ref _notice, value);
            Groups = WithNotice(Groups);
        }
    }

    /// A notice whose tool card is not on the page, such as after the daemon dropped mid-action.
    public AccountsNotice? UnplacedNotice => Groups.Any(g => g.Notice is not null) ? null : _notice;

    void Report(string? vendor, AccountsNotice? notice) {
        _noticeVendor = vendor;
        Notice = notice;
    }

    IReadOnlyList<AccountVendorGroup> WithNotice(IReadOnlyList<AccountVendorGroup> groups) =>
        [.. groups.Select(g => g with { Notice = g.Vendor == _noticeVendor ? _notice : null })];

    public bool IsBusy {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public IReadOnlyList<AccountCandidate> Candidates {
        get => _candidates;
        private set => this.RaiseAndSetIfChanged(ref _candidates, value);
    }

    public AccountSettingsRow? Renaming {
        get => _renaming;
        private set => this.RaiseAndSetIfChanged(ref _renaming, value);
    }

    public string RenameDraft {
        get => _renameDraft;
        set => this.RaiseAndSetIfChanged(ref _renameDraft, value);
    }

    public ReactiveCommand<AccountCandidate, Unit> AddCandidateCommand { get; }
    public ReactiveCommand<string, Unit> AddFromPickerCommand { get; }
    public ReactiveCommand<AccountSettingsRow, Unit> AddDefaultCommand { get; }
    public ReactiveCommand<AccountSettingsRow, Unit> RefreshCommand { get; }
    public ReactiveCommand<AccountSettingsRow, Unit> RewireCommand { get; }
    public ReactiveCommand<AccountSettingsRow, Unit> RemoveCommand { get; }
    public ReactiveCommand<AccountSettingsRow, Unit> BeginRenameCommand { get; }
    public ReactiveCommand<Unit, Unit> CommitRenameCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelRenameCommand { get; }

    bool CanAct => CanManage && !IsBusy;

    /// Discovery reads the disk, so it runs off the UI thread; only the newest read publishes.
    public void RefreshCandidates() => _ = ReadCandidatesAsync();

    async Task ReadCandidatesAsync() {
        var read = ++_candidateReads;
        try {
            var found = await Task.Run(_discover, _token);
            // An account already on the page is managed from its row; offering it again reads as a second account.
            // One folder can hold two vendors' configuration, and each is its own account.
            var listed = Groups.SelectMany(g => g.Rows).Select(r => (r.Vendor, r.Directory)).ToHashSet();
            if (read == _candidateReads && !_token.IsCancellationRequested)
                Candidates = [.. found.Where(c => !listed.Contains((c.Vendor.VendorId, c.Directory)))];
        } catch (OperationCanceledException) when (_token.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not look for account directories: {ex.Message}");
        }
    }

    internal static AccountsNotice? OutcomeNotice(MutationVerb verb, string label, MutationOutcome outcome) {
        var (_, infinitive, doing) = Words(verb);
        var failed = $"Could not {infinitive} {label}";
        return outcome switch {
            MutationOutcome.Succeeded                   => null,
            MutationOutcome.Failed { Reason: { } why }  => new(failed, why),
            MutationOutcome.Failed exit                 => new(failed, $"kcap's command line stopped with an error (code {exit.ExitCode})."),
            MutationOutcome.Refused { Reason: var why } => new(failed, RefusalRemedy(why) is { } remedy ? $"{remedy}." : why),
            // Unconfirmed, and every case the lane never produces for an account verb: none reads as done.
            _                                           => new($"{doing} {label} could not be confirmed", "The list shows the current state.", IsWarning: true),
        };
    }

    // The lane's checks before it runs the CLI.
    static string? RefusalRemedy(string reason) => reason switch {
        "cli_below_floor" or "cli_version_unknown" or "cli_override_missing" => UpdateKcap,
        "cli_not_found"                                                        => "Reinstall kcap to manage accounts",
        "daemon_renamed_restart_app"                                           => "Restart the app to manage accounts",
        _                                                                      => null,
    };

    static (string Done, string Infinitive, string Doing) Words(MutationVerb verb) => verb switch {
        MutationVerb.AccountAdd    => ("Added", "add", "Adding"),
        MutationVerb.AccountRemove => ("Removed", "remove", "Removing"),
        MutationVerb.AccountRename => ("Renamed", "rename", "Renaming"),
        MutationVerb.AccountRewire => ("Re-wired", "re-wire", "Re-wiring"),
        MutationVerb.AccountRefresh => ("Refreshed", "refresh", "Refreshing"),
        _                          => throw new ArgumentOutOfRangeException(nameof(verb), verb, "not an account verb"),
    };

    Task AddCandidateAsync(AccountCandidate candidate) =>
        RunAsync(candidate.Vendor.VendorId, MutationVerb.AccountAdd, AccountRows.LastSegment(candidate.Directory),
            new AccountArgs(candidate.Vendor.VendorId, candidate.Directory, null, null));

    Task AddDefaultAsync(AccountSettingsRow row) =>
        row.IsUnregisteredDefault
            ? RunAsync(row.Vendor, MutationVerb.AccountAdd, row.Label, new AccountArgs(row.Vendor, row.Directory, null, null))
            : Task.CompletedTask;

    Task RewireAsync(AccountSettingsRow row) =>
        row.Id is { } id ? RunAsync(row.Vendor, MutationVerb.AccountRewire, row.Label, new AccountArgs(null, null, id, null)) : Task.CompletedTask;

    Task RefreshAsync(AccountSettingsRow row) =>
        row.Id is { } id ? RunAsync(row.Vendor, MutationVerb.AccountRefresh, row.Label, new AccountArgs(null, null, id, null)) : Task.CompletedTask;

    Task RemoveAsync(AccountSettingsRow row) =>
        row.Id is { } id ? RunAsync(row.Vendor, MutationVerb.AccountRemove, row.Label, new AccountArgs(null, null, id, null)) : Task.CompletedTask;

    async Task AddFromPickerAsync(string vendor) {
        if (!CanAct) return;
        IsBusy = true;
        Report(vendor, null);
        try {
            string? directory;
            try {
                directory = await _pickDirectory(_token);
            } catch (OperationCanceledException) when (_token.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                Report(vendor, new("Could not open the folder picker", ex.Message));
                return;
            }
            if (directory is not { Length: > 0 }) return;
            await SendAsync(vendor, MutationVerb.AccountAdd, AccountRows.LastSegment(directory), new AccountArgs(vendor, directory, null, null));
        } finally { IsBusy = false; }
    }

    void BeginRename(AccountSettingsRow row) {
        if (!CanAct || row.IsUnregisteredDefault) return;
        RenameDraft = row.Label;
        Renaming = row;
    }

    async Task CommitRenameAsync() {
        if (!CanAct || Renaming is not { Id: { } id } row || !RenameChanges(row, RenameDraft)) return;
        var label = RenameDraft.Trim();
        Renaming = null;
        await RunAsync(row.Vendor, MutationVerb.AccountRename, $"{row.Label} to {label}", new AccountArgs(null, null, id, label));
    }

    static bool RenameChanges(AccountSettingsRow? row, string? draft) =>
        row is not null && !string.IsNullOrWhiteSpace(draft) && !string.Equals(draft.Trim(), row.Label, StringComparison.Ordinal);

    async Task RunAsync(string vendor, MutationVerb verb, string label, AccountArgs args) {
        if (!CanAct) return;
        IsBusy = true;
        Report(vendor, null);
        try {
            await SendAsync(vendor, verb, label, args);
        } finally { IsBusy = false; }
    }

    async Task SendAsync(string vendor, MutationVerb verb, string label, AccountArgs args) {
        var request = MutationRequestFactory.ForAccount(verb, args, _daemonName, _profileName);
        try {
            Report(vendor, OutcomeNotice(verb, label, await _runMutation(request, _token)));
        } catch (OperationCanceledException) when (_token.IsCancellationRequested) {
        } catch (Exception ex) {
            Report(vendor, new($"Could not {Words(verb).Infinitive} {label}", ex.Message));
        }
    }

    IReadOnlyList<AccountVendorGroup> GroupsFor(AccountsSnapshotDto? snapshot) {
        if (snapshot is null) return [];

        var accounts = (snapshot.Accounts ?? []).DistinctBy(a => a.Id).ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var gone in _slots.Keys.Where(id => !accounts.ContainsKey(id)).ToArray()) _slots.Remove(gone);

        AccountUsageSlot SlotFor(string id) => _slots.TryGetValue(id, out var slot) ? slot : _slots[id] = new AccountUsageSlot();

        return [
            .. Vendors.Select(vendor => new AccountVendorGroup(vendor.VendorId, HarnessRegistry.LabelOf(vendor), [
                .. AccountRows.For(snapshot, vendor.VendorId).Select(r => r.Id is { } id && accounts.TryGetValue(id, out var account)
                    ? new AccountSettingsRow(id, r.Vendor, r.Directory, r.Label, r.RecordingState,
                        account.Email, account.Plan, account.RefreshError ?? account.LimitsError, SlotFor(id), r.IsDefault)
                    : new AccountSettingsRow(r.Id, r.Vendor, r.Directory, r.Label, r.RecordingState, null, null, null, _defaultSlot, r.IsDefault)),
            ])),
        ];
    }

    void UpdateUsage() {
        var accounts = _snapshot?.Accounts ?? [];
        if (_now is not { } now) {
            SetCards(accounts, null);
            return;
        }

        foreach (var account in accounts)
            if (_slots.TryGetValue(account.Id, out var slot)) slot.View = AccountUsage.Project(account, now, _zone);

        SetCards(accounts, now);
    }

    void SetCards(AccountViewDto[] accounts, DateTimeOffset? now) {
        var ordered = accounts.OrderBy(a => a.Label, StringComparer.Ordinal).ThenBy(a => a.Directory, StringComparer.Ordinal).ToArray();

        var ready = now is { } at
            ? ordered.Where(a => AccountUsage.Project(a, at, _zone).Ready).Select(a => a.Label).ToArray()
            : [];
        ReadyCard = new($"{ready.Length} of {ordered.Length}", ready.Length == 0 ? null : string.Join(", ", ready), false);

        var next = now is { } n
            ? ordered.Select(a => (Account: a, Window: AccountUsage.NextResetWindow(a, n))).Where(x => x.Window is not null)
                .OrderBy(x => x.Window!.ResetsAt).FirstOrDefault()
            : default;
        NextResetCard = next.Window is { ResetsAt: { } resets } window
            ? new(UsageText.Reset(resets, now!.Value, _zone), $"{next.Account.Label} · {UsageText.WindowLabel(window.WindowMinutes)}", false)
            : new("—", null, false);

        var fixes = ordered.Select(a => (Account: a, Reason: AccountUsage.NeedsFix(a))).Where(x => x.Reason is not null).ToArray();
        NeedsFixCard = new(fixes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            fixes.Length == 0 ? null : $"{fixes[0].Account.Label} · {fixes[0].Reason}", fixes.Length > 0);
    }

    // A resent snapshot listing the same rows leaves the list alone, so an open menu or a rename in
    // progress is not rebuilt under the person using it.
    void ApplyGroups(IReadOnlyList<AccountVendorGroup> groups) {
        var same = groups.Count == Groups.Count && groups.Zip(Groups).All(pair =>
            pair.First.Vendor == pair.Second.Vendor && pair.First.Rows.SequenceEqual(pair.Second.Rows));
        if (!same) Groups = WithNotice(groups);
        if (Renaming is { } renaming)
            Renaming = Groups.SelectMany(g => g.Rows).FirstOrDefault(r => r.Id is not null && r.Id == renaming.Id);
    }

    void Refresh() {
        CanManage = _supported && _snapshot is not null;
        this.RaisePropertyChanged(nameof(ReadOnlyNotice));
    }

    public void Dispose() {
        _lifetime.Cancel();
        _subscriptions.Dispose();
        _lifetime.Dispose();
    }
}

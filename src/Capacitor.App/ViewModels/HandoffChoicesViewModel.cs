using System.Collections.Frozen;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The hand-off strip's chips. A hand-off always launches on this machine's daemon, so the agents
/// and models are the local daemon's, and no account is offered.
public sealed class HandoffChoicesViewModel : ReactiveObject, ILaunchChoices, IDisposable {
    readonly CompositeDisposable _disposables = new();
    readonly ObservableAsPropertyHelper<IReadOnlyList<HarnessOption>> _harnesses;
    readonly ObservableAsPropertyHelper<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>> _catalog;

    /// sessionVendor is the ended session's own agent, kept while this daemon hosts it.
    public HandoffChoicesViewModel(
            IDaemonClientService daemon, string? sessionVendor,
            IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>? serverCatalog = null) {
        _selectedVendor = string.IsNullOrWhiteSpace(sessionVendor) ? HomeViewModel.DefaultVendor : sessionVendor;

        // ObserveOn before ToProperty: snapshots arrive on the daemon client's own thread.
        var options = daemon.Snapshots.Select(s => HostedHarnessCatalog.Build(s.Daemon.SupportedVendors));
        _harnesses = options
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Harnesses, HostedHarnessCatalog.Build(null))
            .DisposeWith(_disposables);
        options
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Revalidate)
            .DisposeWith(_disposables);

        _catalog = daemon.Snapshots
            .Select(s => VendorModelMaps.FromStatus(s.Daemon.VendorModels))
            .StartWith((IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>?) null)
            .CombineLatest(serverCatalog ?? Observable.Return(ServerVendorModelCatalog.Empty), HomeViewModel.MergeCatalogs)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.EffectiveModelCatalog, ServerVendorModelCatalog.Empty)
            .DisposeWith(_disposables);

        RetryAccountsCommand = ReactiveCommand.Create(() => { });
    }

    public IReadOnlyList<HarnessOption> Harnesses => _harnesses.Value;

    string _selectedVendor;
    public string SelectedVendor {
        get => _selectedVendor;
        private set => this.RaiseAndSetIfChanged(ref _selectedVendor, value);
    }

    string _selectedModel = "";
    public string SelectedModel {
        get => _selectedModel;
        set => this.RaiseAndSetIfChanged(ref _selectedModel, value);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> EffectiveModelCatalog => _catalog.Value;

    public string? UnavailableHarnessTip(HarnessOption harness) =>
        harness.Available ? null : HomeViewModel.HarnessUnavailableReason(harness.Label, null);

    public IReadOnlyList<ModelChoice> ModelChoicesFor(string vendor) =>
        EffectiveModelCatalog.TryGetValue(vendor, out var models) ? models : HostedHarnessCatalog.ModelChoicesFor(vendor);

    public Task ChooseHarnessAsync(string vendor) {
        SetVendor(vendor);
        return Task.CompletedTask;
    }

    string? _selectedEffort;
    public string? SelectedEffort {
        get => _selectedEffort;
        set => this.RaiseAndSetIfChanged(ref _selectedEffort, value);
    }

    string _selectedPermissionMode = HomeViewModel.DefaultPermissionMode;
    public string SelectedPermissionMode {
        get => _selectedPermissionMode;
        set => this.RaiseAndSetIfChanged(ref _selectedPermissionMode, value);
    }

    public Task ChoosePermissionModeAsync(string mode) {
        SelectedPermissionMode = mode;
        return Task.CompletedTask;
    }

    string _selectedCodexSandbox = HostedHarnessCatalog.DefaultCodexSandbox;
    public string SelectedCodexSandbox {
        get => _selectedCodexSandbox;
        set => this.RaiseAndSetIfChanged(ref _selectedCodexSandbox, value);
    }

    string _selectedCodexApproval = HostedHarnessCatalog.DefaultCodexApproval;
    public string SelectedCodexApproval {
        get => _selectedCodexApproval;
        set => this.RaiseAndSetIfChanged(ref _selectedCodexApproval, value);
    }

    public bool AccountSection => false;
    public IReadOnlyList<AccountChoice> AccountRows => [];
    public string? SelectedAccountLabel => null;
    public string? AccountPendingText => null;
    public bool AccountRetryVisible => false;
    public IReadOnlyDictionary<string, AccountUsageView> AccountUsageById => FrozenDictionary<string, AccountUsageView>.Empty;
    public Task PickAccountAsync(string? accountId) => Task.CompletedTask;
    public ReactiveCommand<Unit, Unit> RetryAccountsCommand { get; }

    /// The chips a draft was sent with. The vendor goes through the same check a snapshot does.
    public void Restore(HandoffDraft draft) {
        SetVendor(draft.Vendor);
        Revalidate(Harnesses);
        if (string.Equals(SelectedVendor, draft.Vendor, StringComparison.OrdinalIgnoreCase)) SelectedModel = draft.Model;
        SelectedEffort = draft.Effort;
        if (draft.PermissionMode is { } mode) SelectedPermissionMode = mode;
        if (draft.CodexPosture is { } posture) {
            SelectedCodexSandbox = posture.Sandbox;
            SelectedCodexApproval = posture.Approval;
        }
    }

    public HandoffDraft Capture(string text, IReadOnlyList<StagedAttachment> attachments) =>
        new(text, attachments, SelectedVendor, SelectedModel, SelectedEffort, SelectedPermissionMode,
            new CodexLaunchPosture(SelectedCodexSandbox, SelectedCodexApproval), null);

    /// Model ids are vendor-specific, so a vendor change drops the model.
    void SetVendor(string vendor) {
        if (!string.Equals(vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase)) SelectedModel = "";
        SelectedVendor = vendor;
    }

    void Revalidate(IReadOnlyList<HarnessOption> options) {
        var current = options.FirstOrDefault(o => string.Equals(o.Vendor, SelectedVendor, StringComparison.OrdinalIgnoreCase));
        if (current is { Available: true }) return;
        SetVendor(options.Any(o => o.Available && o.Vendor == HomeViewModel.DefaultVendor)
            ? HomeViewModel.DefaultVendor
            : options.FirstOrDefault(o => o.Available)?.Vendor ?? HomeViewModel.DefaultVendor);
    }

    public void Dispose() => _disposables.Dispose();
}

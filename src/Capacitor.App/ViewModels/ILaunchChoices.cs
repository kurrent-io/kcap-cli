using System.ComponentModel;
using System.Reactive;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// What the agent, effort, permission and Codex posture chips read and set. The launcher's
/// HomeViewModel implements it; the account members describe the Agent panel's Account section,
/// which stays hidden while AccountSection is false.
public interface ILaunchChoices : INotifyPropertyChanged {
    IReadOnlyList<HarnessOption> Harnesses { get; }
    string SelectedVendor { get; }
    string SelectedModel { get; set; }
    IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>> EffectiveModelCatalog { get; }
    IReadOnlyList<ModelChoice> ModelChoicesFor(string vendor);
    Task ChooseHarnessAsync(string vendor);
    /// The picker's hover text for a vendor the target daemon does not advertise; null for one it does.
    string? UnavailableHarnessTip(HarnessOption harness);

    string? SelectedEffort { get; set; }
    string SelectedPermissionMode { get; set; }
    string SelectedCodexSandbox { get; set; }
    string SelectedCodexApproval { get; set; }

    bool AccountSection { get; }
    IReadOnlyList<AccountChoice> AccountRows { get; }
    string? SelectedAccountLabel { get; }
    string? AccountPendingText { get; }
    bool AccountRetryVisible { get; }
    IReadOnlyDictionary<string, AccountUsageView> AccountUsageById { get; }
    Task PickAccountAsync(string? accountId);
    ReactiveCommand<Unit, Unit> RetryAccountsCommand { get; }
}

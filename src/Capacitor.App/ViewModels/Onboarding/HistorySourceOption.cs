using Capacitor.Cli.Core.Harness;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Onboarding;

/// One harness whose history the History page found, and whether its sessions are part of the import.
/// Independent of whether the harness records from now on.
public sealed class HistorySourceOption : ReactiveObject {
    bool _isSelected = true;

    internal HistorySourceOption(HarnessId id, int sessions) {
        Id       = id;
        Label    = HarnessRegistry.LabelOf(id);
        Sessions = sessions;
    }

    public HarnessId Id       { get; }
    public string    Label    { get; }
    public int       Sessions { get; }

    public string SignalLine => HistoryStepViewModel.SessionCount(Sessions);

    public string AutomationName => $"Import history from {Label}";

    public bool IsSelected {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

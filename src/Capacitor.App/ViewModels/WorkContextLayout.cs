using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Section folds a reader sets in one session's work context and expects in the next: one instance
/// for the app's lifetime, shared by every workspace's pane.
public sealed class WorkContextLayout : ReactiveObject {
    bool _sessionExpanded;
    public bool SessionExpanded { get => _sessionExpanded; set => this.RaiseAndSetIfChanged(ref _sessionExpanded, value); }
}

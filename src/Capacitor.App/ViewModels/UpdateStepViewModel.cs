using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Keeps a step's visual identity while its projected progress changes.
public sealed class UpdateStepViewModel(UpdateStep step) : ReactiveObject {
    UpdateStep _step = step;

    public UpdateStep Step {
        get => _step;
        internal set => this.RaiseAndSetIfChanged(ref _step, value);
    }
}

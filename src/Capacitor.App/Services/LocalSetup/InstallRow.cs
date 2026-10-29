using ReactiveUI.Reactive;

namespace Capacitor.App.Services.LocalSetup;

public sealed class InstallRow : ReactiveObject {
    RowState _state;

    public RowState State {
        get => _state;
        internal set => this.RaiseAndSetIfChanged(ref _state, value);
    }
}

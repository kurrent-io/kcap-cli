using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// A session header's title and its in-place rename. The field marks its own presses handled so
/// a header that drags the window on a blank press never starts a drag from inside it.
public partial class SessionTitleEditor : UserControl {
    SessionTitleEditorViewModel? _model;

    public SessionTitleEditor() {
        InitializeComponent();
        TitleInput.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        TitleInput.AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) => {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as SessionTitleEditorViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
        };
    }

    // Entering edit mode selects the whole title; a failed save hands the field back as it was.
    void OnModelChanged(object? sender, PropertyChangedEventArgs e) {
        var entering = e.PropertyName == nameof(SessionTitleEditorViewModel.IsEditing);
        if (!entering && e.PropertyName != nameof(SessionTitleEditorViewModel.IsSaving)) return;
        if (sender is not SessionTitleEditorViewModel { IsEditing: true, IsSaving: false }) return;
        Dispatcher.UIThread.Post(() => {
            if (!ReferenceEquals(sender, _model) || !_model.IsInputEnabled) return;
            TitleInput.Focus();
            if (entering) TitleInput.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    void OnInputKeyDown(object? sender, KeyEventArgs e) {
        if (_model is null) return;
        ICommand? command = e.Key switch {
            Key.Enter => _model.SaveCommand,
            Key.Escape => _model.CancelCommand,
            _ => null,
        };
        if (command is null) return;
        e.Handled = true;
        if (command.CanExecute(null)) command.Execute(null);
    }
}

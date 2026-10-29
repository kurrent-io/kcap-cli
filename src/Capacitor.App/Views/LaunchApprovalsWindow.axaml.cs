using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public partial class LaunchApprovalsWindow : Window {
    ActivityViewModel? _activity;

    public LaunchApprovalsWindow() {
        InitializeComponent();
        Closed += (_, _) => {
            _activity?.OnTabVisibleChanged(false);
            _activity = null;
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty && change.Property != WindowStateProperty
            && change.Property != DataContextProperty) return;
        if (_activity != DataContext as ActivityViewModel) {
            _activity?.OnTabVisibleChanged(false);
            _activity = DataContext as ActivityViewModel;
        }
        _activity?.OnTabVisibleChanged(IsVisible && WindowState != WindowState.Minimized);
    }
}

using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

public partial class SettingsWindow : Window {
    const double UnscaledMinWidth = 760;
    const double UnscaledMinHeight = 520;

    UiScaleSetting? _contentScale;
    IDisposable? _contentScaleSubscription;

    public SettingsWindow() {
        InitializeComponent();
        // Coming back from System Settings is an activation, and the only signal that access changed.
        Activated += (_, _) => (DataContext as SettingsViewModel)?.RefreshNotificationAccess();
        Closed += (_, _) => _contentScaleSubscription?.Dispose();
    }

    /// The text size the main window uses, followed live so the Appearance page previews its own change.
    public UiScaleSetting? ContentScale {
        get => _contentScale;
        set {
            _contentScale = value;
            _contentScaleSubscription?.Dispose();
            _contentScaleSubscription = value?.Changes
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ApplyScale);
        }
    }

    void ApplyScale(double scale) {
        ScaleHost.LayoutTransform = scale == UiScale.Default ? null : new ScaleTransform(scale, scale);
        var area = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        var available = area is null ? (Size?)null : area.WorkingArea.Size.ToSize(area.Scaling);
        MinWidth = UiScale.Minimum(UnscaledMinWidth, scale, available?.Width);
        MinHeight = UiScale.Minimum(UnscaledMinHeight, scale, available?.Height);
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    void OnProfileCardClick(object? sender, RoutedEventArgs e) {
        if (ProfilesTab.IsVisible) SettingsTabs.SelectedItem = ProfilesTab;
    }

    public void ShowSearch() => SettingsTabs.SelectedItem = SearchTab;
}

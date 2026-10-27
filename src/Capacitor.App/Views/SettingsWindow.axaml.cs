using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

public partial class SettingsWindow : Window {
    const double UnscaledMinWidth = 760;
    const double UnscaledMinHeight = 520;

    UiScaleSetting? _contentScale;
    IDisposable? _contentScaleSubscription;
    double _scale = UiScale.Default;

    public SettingsWindow() {
        InitializeComponent();
        // Coming back from System Settings is an activation, and the only signal that access changed.
        Activated += (_, _) => (DataContext as SettingsViewModel)?.RefreshNotificationAccess();
        // A move, a display scaling change and a resized work area each change what the screen offers.
        PositionChanged += (_, _) => RefreshMinimumSize();
        ScalingChanged += (_, _) => RefreshMinimumSize();
        Screens? watchedScreens = null;
        void OnScreensChanged(object? sender, EventArgs e) => RefreshMinimumSize();
        Opened += (_, _) => {
            if (watchedScreens is not null || Screens is not { } screens) return;
            watchedScreens = screens;
            screens.Changed += OnScreensChanged;
        };
        Closed += (_, _) => {
            _contentScaleSubscription?.Dispose();
            if (watchedScreens is not null) watchedScreens.Changed -= OnScreensChanged;
        };
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
        _scale = scale;
        ScaleHost.LayoutTransform = scale == UiScale.Default ? null : new ScaleTransform(scale, scale);
        ApplyMinimumSize();
    }

    // A closed window has no platform window to ask a screen about.
    void RefreshMinimumSize() {
        if (_scale != UiScale.Default && PlatformImpl is not null) ApplyMinimumSize();
    }

    // Bounded by the screen the window is on.
    void ApplyMinimumSize() {
        var area = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        var available = area is null ? (Size?)null : area.WorkingArea.Size.ToSize(area.Scaling);
        MinWidth = UiScale.Minimum(UnscaledMinWidth, _scale, available?.Width);
        MinHeight = UiScale.Minimum(UnscaledMinHeight, _scale, available?.Height);
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    void OnProfileCardClick(object? sender, RoutedEventArgs e) {
        if (ProfilesTab.IsVisible) SettingsTabs.SelectedItem = ProfilesTab;
    }

    async void OnCopyVersionInfo(object? sender, RoutedEventArgs e) {
        if ((DataContext as SettingsViewModel)?.About is not { } about || Clipboard is not { } clipboard) return;
        try {
            await clipboard.SetTextAsync(about.VersionReport);
            AboutCopyText.Text = "Copied";
            AboutCopyIcon.Kind = Lucide.Avalonia.LucideIconKind.Check;
        } catch (Exception) {
            AboutCopyText.Text = "Copy failed";
        }
        DispatcherTimer.RunOnce(() => {
            AboutCopyText.Text = "Copy version info";
            AboutCopyIcon.Kind = Lucide.Avalonia.LucideIconKind.Copy;
        }, TimeSpan.FromSeconds(1.5));
    }

    public void ShowSearch() => SettingsTabs.SelectedItem = SearchTab;
}

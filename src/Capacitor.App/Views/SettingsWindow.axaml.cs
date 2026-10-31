using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

public partial class SettingsWindow : Window {
    const double UnscaledMinWidth = 760;
    const double UnscaledMinHeight = 520;
    /// The page header row's height in SettingsStyles, which the drag strip matches.
    internal const double UnscaledChromeHeight = 40;

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
        SettingsTabs.TemplateApplied += (_, e) => {
            _compactHeader = e.NameScope.Find<Border>("PART_CompactHeader");
            _compactTitle = e.NameScope.Find<TextBlock>("PART_CompactTitle");
            ShowCompactTitle(0, null);
        };
        AddHandler(ScrollViewer.ScrollChangedEvent, OnPageScrolled, RoutingStrategies.Bubble);
        SettingsTabs.SelectionChanged += (_, _) => {
            _pageScroll = null;
            Dispatcher.UIThread.Post(SyncCompactTitle, DispatcherPriority.Loaded);
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
        ChromeStrip.Height = UnscaledChromeHeight * scale;
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

    // Scrolled this far, the large title has gone under the strip and only the compact one is left.
    const double TitleHandoff = 80;
    Border? _compactHeader;
    TextBlock? _compactTitle;
    ScrollViewer? _pageScroll;

    void OnPageScrolled(object? sender, ScrollChangedEventArgs e) {
        if (e.Source is ScrollViewer page && ReferenceEquals(page, _pageScroll ??= SelectedPageScroll())) ShowCompactTitle(page);
    }

    ScrollViewer? SelectedPageScroll() =>
        (SettingsTabs.SelectedContent as Visual)?.GetSelfAndVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    void SyncCompactTitle() {
        _pageScroll = SelectedPageScroll();
        if (_pageScroll is null) ShowCompactTitle(0, null);
        else ShowCompactTitle(_pageScroll);
    }

    void ShowCompactTitle(ScrollViewer page) {
        var title = page.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("kcapTitle"));
        var linear = Math.Clamp((page.Offset.Y - 8) / TitleHandoff, 0, 1);
        // Smoothstep: the handoff eases in and out instead of starting and stopping at full speed.
        var progress = linear * linear * (3 - 2 * linear);
        if (title is not null) title.Opacity = 1 - progress;
        ShowCompactTitle(progress, title?.Text);
    }

    void ShowCompactTitle(double progress, string? text) {
        if (_compactTitle is null || _compactHeader is null) return;
        _compactTitle.Text = text;
        _compactTitle.Opacity = progress;
        _compactTitle.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translateY({(1 - progress) * 8}px)"));
        _compactHeader.BorderBrush = progress > 0 ? (IBrush?)this.FindResource("KcapBorderBrush") : Brushes.Transparent;
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    void OnProfileCardClick(object? sender, RoutedEventArgs e) {
        if ((DataContext as SettingsViewModel)?.Profiles is not null) SettingsTabs.SelectedItem = ProfilesTab;
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

    public void ShowPage(SettingsTab page) {
        if (page == SettingsTab.Search) ShowSearch();
        else if (page == SettingsTab.Harnesses) SettingsTabs.SelectedItem = HarnessesTab;
    }
}

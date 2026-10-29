using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using ReactiveUI.Reactive;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

// ReactiveWindow<T> ties ViewModel.Activator to THIS window's own Loaded/Unloaded lifecycle
// (AvaloniaActivationForViewFetcher) — no manual Activator.Activate() call is needed; Show()
// activates the VM's WhenActivated projections, Close() deactivates them.
public partial class MainWindow : ReactiveWindow<MainWindowViewModel> {
    /// Assigned by MainWindowCoordinator on every window it builds: returns true when
    /// the close must be intercepted — the coordinator hides the window and the close below is
    /// cancelled. Left null on a plainly-constructed window (tests), where a close is a real
    /// close.
    public Func<bool>? CloseInterceptor { get; set; }

    /// The onboarding flow shown in place of the rail and launcher. Wizard-first mode builds this
    /// window with no MainWindowViewModel, so the pane gets its own DataContext rather than the
    /// window's; clearing it hands the window over to the main surface.
    public OnboardingViewModel? Onboarding {
        get => OnboardingPane.DataContext as OnboardingViewModel;
        set {
            OnboardingPane.DataContext = value;
            OnboardingPane.IsVisible   = value is not null;
            SessionsSurface.IsVisible  = value is null;
            ApplyMinimumSize();
        }
    }

    public PaneLayout Panes { get; } = new();

    ColumnDefinition RailColumn => SessionsSurface.ColumnDefinitions[0];

    // Wide enough on macOS to hold the window buttons, which overlay the window's top-left corner.
    static readonly double RailStripWidth = OperatingSystem.IsMacOS() ? 78 : 44;

    void OnRailSplitterDragDelta(object? sender, VectorEventArgs e) =>
        SessionRail.Opacity = PaneLayout.HidesAt(RailColumn.ActualWidth, PaneLayout.RailMinWidth) ? PaneLayout.HidingOpacity : 1;

    // A drag resizes the column live; Panes takes the width once, when it ends. Released below the
    // minimum, the rail hides and reopens at the minimum.
    void OnRailSplitterDragCompleted(object? sender, VectorEventArgs e) {
        SessionRail.Opacity = 1;
        var width = RailColumn.ActualWidth;
        var hides = PaneLayout.HidesAt(width, PaneLayout.RailMinWidth);
        Panes.RailWidth = hides ? PaneLayout.RailMinWidth : width;
        if (hides) Panes.RailCollapsed = true;
    }

    void OnRailExpandClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Panes.RailCollapsed = false;

    void ApplyRail() {
        var collapsed = Panes.RailCollapsed;
        RailColumn.Width = new GridLength(collapsed ? RailStripWidth : Panes.RailWidth);
        SessionRail.IsVisible = !collapsed;
        RailSplitter.IsVisible = !collapsed;
        RailStrip.IsVisible = collapsed;
    }

    WindowNotificationManager? _notifications;
    IDisposable? _notifierSubscription;
    IAppNotifier? _notifier;

    PullRequestContextViewModel? _foregroundPullRequests;

    /// Assigned by App.BuildAndShowMainWindow — the SAME IAppNotifier instance
    /// AgentActionService pushes into, so the toast overlay and stderr mirroring are always in
    /// sync. Left null on a plainly-constructed window (tests that don't exercise toasts).
    public IAppNotifier? Notifier {
        get => _notifier;
        set {
            _notifier = value;
            _notifierSubscription?.Dispose();
            _notifierSubscription = value?.Messages
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(ShowToast);
        }
    }

    UiScaleSetting? _contentScale;
    IDisposable? _contentScaleSubscription;

    /// Magnifies everything in the window. Assigned by App; left null on a plainly-constructed
    /// window, whose zoom shortcuts then do nothing.
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

    double _scale = UiScale.Default;
    double _captionTop;

    void ApplyScale(double scale) {
        _scale = scale;
        ScaleHost.LayoutTransform = scale == UiScale.Default ? null : new ScaleTransform(scale, scale);
        ApplyMinimumSize();
        ApplyCaptionInset();
    }

    // A closed window has no platform window to ask a screen about.
    void RefreshMinimumSize() {
        if (_scale != UiScale.Default && PlatformImpl is not null) ApplyMinimumSize();
    }

    // Bounded by the screen the window is on.
    void ApplyMinimumSize() {
        var area = Screens?.ScreenFromWindow(this) ?? Screens?.Primary;
        var available = area is null ? (Size?)null : area.WorkingArea.Size.ToSize(area.Scaling);
        var onboarding = Onboarding is not null;
        MinWidth  = UiScale.Minimum(onboarding ? WindowSizeMemory.OnboardingMinWidth : WindowSizeMemory.MinWidth, _scale, available?.Width);
        MinHeight = UiScale.Minimum(onboarding ? WindowSizeMemory.OnboardingMinHeight : WindowSizeMemory.MinHeight, _scale, available?.Height);
    }

    // The inset is in window units and the pane sits inside the scale, so it is divided back out.
    void ApplyCaptionInset() => ContentPane.Margin = new Thickness(0, _captionTop / _scale, 0, 0);

    public MainWindow() {
        InitializeComponent();
        SessionsSurface.SizeChanged += (_, e) => RailColumn.MaxWidth = e.NewSize.Width * PaneLayout.MaxShare;
        SessionRail.CollapseRequested += (_, _) => Panes.RailCollapsed = true;
        Panes.Changed += (_, _) => ApplyRail();
        ApplyRail();
        KeyBindings.AddRange(ZoomShortcut.For(this));
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
        // Hide-to-tray never closes the window, so only a real close lets the setting and the screens go.
        Closed += (_, _) => {
            _contentScaleSubscription?.Dispose();
            if (watchedScreens is not null) watchedScreens.Changed -= OnScreensChanged;
        };
        BindRefreshShortcuts();
        BindSettingsShortcut();
        KeepContentClearOfCaptionButtons();
        BindFullScreenShortcut();
        Closing += (_, e) => {
            if (CloseInterceptor?.Invoke() == true) e.Cancel = true;
        };

        // Built on Loaded (window open), not here in the constructor: WindowNotificationManager
        // self-installs into the TopLevel's AdornerLayer once its template is applied, and Loaded
        // is when that is guaranteed. A hide-to-tray/reopen cycle (MainWindowCoordinator.Hide())
        // only toggles OS-level visibility — it never detaches this window from the visual tree —
        // so Loaded fires once per window instance and this null-guard is defensive only.
        Loaded += (_, _) => _notifications ??= new WindowNotificationManager(this) {
            Position = NotificationPosition.TopRight,
        };

        var switchCover = new WorkspaceSwitchCover(ContentPane, SwitchCover, SwitchCoverStill);
        this.WhenActivated(disposables => {
            var coverTracking = new SerialDisposable().DisposeWith(disposables);
            this.WhenAnyValue(x => x.ViewModel)
                .Subscribe(vm => coverTracking.Disposable = vm is null ? null : switchCover.Track(vm))
                .DisposeWith(disposables);
            // Switched over the ViewModel, not read once: an onboarding window activates before
            // the main surface hands it one.
            Observable.Switch(this.WhenAnyValue(x => x.ViewModel)
                    .Select(vm => vm is null
                        ? Observable.Return<ISessionWorkspace?>(null)
                        : vm.WhenAnyValue(x => x.CurrentWorkspace)))
                .Subscribe(workspace => {
                    if (workspace is null) Dispatcher.UIThread.Post(LauncherView.FocusComposer, DispatcherPriority.Loaded);
                    UpdateWorkspaceVisibility();
                })
                .DisposeWith(disposables);
        });
    }

    // Meta is Command. Ctrl+R is added only where there is no Command key, and it yields while
    // the terminal has focus. Ctrl+Shift+R does not yield. macOS leaves Ctrl+R unbound.
    void BindRefreshShortcuts() {
        var bindings = new List<KeyBinding>();
        Add(new KeyGesture(Key.R, KeyModifiers.Meta));
        if (RefreshShortcut.FromTerminal is { } fromTerminal) {
            KeyBindings.Add(new KeyBinding {
                Gesture = RefreshShortcut.Primary,
                Command = new RefreshUnlessTerminalFocused(this),
            });
            Add(fromTerminal);
        }

        void Apply() {
            if ((DataContext as MainWindowViewModel)?.RefreshWorkCommand is not { } command) return;
            foreach (var binding in bindings) binding.Command = command;
        }
        Apply();
        DataContextChanged += (_, _) => Apply();

        void Add(KeyGesture gesture) {
            var binding = new KeyBinding { Gesture = gesture };
            bindings.Add(binding);
            KeyBindings.Add(binding);
        }
    }

    // Windows draws its caption buttons over the top-right of the extended client area, so the
    // content pane starts below the title bar there.
    void KeepContentClearOfCaptionButtons() {
        if (!OperatingSystem.IsWindows()) return;
        this.GetObservable(WindowDecorationMarginProperty)
            .Subscribe(margin => {
                _captionTop = margin.Top;
                ApplyCaptionInset();
            });
    }

    // F11 is the Windows convention for full screen; Win+Up and Win+Down already maximize and
    // minimize, which the macOS Window menu has to provide itself.
    void BindFullScreenShortcut() {
        if (!OperatingSystem.IsWindows()) return;
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F11), Command = ReactiveCommand.Create(ToggleFullScreen) });
    }

    WindowState _beforeFullScreen = WindowState.Normal;

    internal void ToggleFullScreen() {
        if (WindowState == WindowState.FullScreen) {
            WindowState = _beforeFullScreen;
            return;
        }
        _beforeFullScreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        WindowState = WindowState.FullScreen;
    }

    // macOS takes ⌘, from the native app menu, which Windows does not draw.
    void BindSettingsShortcut() {
        if (!OperatingSystem.IsWindows()) return;
        var binding = new KeyBinding { Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Control) };
        KeyBindings.Add(binding);

        void Apply() {
            if ((DataContext as MainWindowViewModel)?.OpenSettingsCommand is { } command) binding.Command = command;
        }
        Apply();
        DataContextChanged += (_, _) => Apply();
    }

    // A toast fired before Loaded, or while the window is hidden (Hide() suspends rendering
    // entirely), is invisible to the user — stderr (AppNotifier's own mirroring) is the only
    // channel that survives either case.
    void ShowToast(string message) =>
        _notifications?.Show(new Notification("kcap", message, NotificationType.Warning, TimeSpan.FromSeconds(4)));

    // The launcher header strip and the bottom-most drag strip — see WindowChrome.
    void OnChromePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) =>
        WindowChrome.BeginDrag(this, e);

    // IsVisible is what Show()/Hide() toggle, and hide-to-tray never fires Closed/Opened, so this
    // property is the one signal that tracks on-screen state across a hide/reopen cycle.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        // DataContextProperty too — defensive: production always assigns DataContext before the
        // first Show(), but this keeps the check correct even if a caller (a test) does it the
        // other way around.
        if (change.Property == IsVisibleProperty || change.Property == DataContextProperty
            || change.Property == WindowStateProperty) UpdateWorkspaceVisibility();
    }

    // Visibility, never keyboard focus, keeps a PR reader's access lease renewed beside another app.
    void UpdateWorkspaceVisibility() {
        if (DataContext is MainWindowViewModel vm) {
            var pullRequests = vm.CurrentWorkspace?.PullRequests;
            if (_foregroundPullRequests != pullRequests) _foregroundPullRequests?.SetForeground(false);
            _foregroundPullRequests = pullRequests;
            pullRequests?.SetForeground(IsVisible && WindowState != Avalonia.Controls.WindowState.Minimized);
        } else {
            _foregroundPullRequests?.SetForeground(false);
            _foregroundPullRequests = null;
        }
    }
}

using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
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
        }
    }

    WindowNotificationManager? _notifications;
    IDisposable? _notifierSubscription;
    IAppNotifier? _notifier;

    // Defaults to false — the Activity flyout starts closed (MainWindow.axaml), so Activity is off
    // regardless of the window's own visibility.
    bool _activityOpen;
    WorkspaceViewModel? _foregroundWorkspace;

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

    public MainWindow() {
        InitializeComponent();
        BindRefreshShortcuts();
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

        // The Activity feed rides a flyout off its chip: open/closed IS the section's
        // expanded/collapsed state for the polling gate below.
        if (ActivityButton.Flyout is { } activityFlyout) {
            activityFlyout.Opened += (_, _) => { _activityOpen = true; UpdateActivityVisibility(); };
            activityFlyout.Closed += (_, _) => { _activityOpen = false; UpdateActivityVisibility(); };
        }

        this.WhenActivated(disposables => {
            // Switched over the ViewModel, not read once: an onboarding window activates before
            // the main surface hands it one.
            Observable.Switch(this.WhenAnyValue(x => x.ViewModel)
                    .Select(vm => vm is null
                        ? Observable.Return<ISessionWorkspace?>(null)
                        : vm.WhenAnyValue(x => x.CurrentWorkspace)))
                .Subscribe(workspace => {
                    // A popup can't meaningfully survive the pane swapping under it — opening a
                    // workspace closes the feed; its Closed handler then turns the gate off.
                    if (workspace is not null) ActivityButton.Flyout?.Hide();
                    UpdateActivityVisibility();
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

    // A toast fired before Loaded, or while the window is hidden (Hide() suspends rendering
    // entirely), is invisible to the user — stderr (AppNotifier's own mirroring) is the only
    // channel that survives either case.
    void ShowToast(string message) =>
        _notifications?.Show(new Notification("Kurrent Capacitor", message, NotificationType.Warning, TimeSpan.FromSeconds(4)));

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
            || change.Property == WindowStateProperty) UpdateActivityVisibility();
    }

    // Activity polls only while it is actually on screen: window visible AND the launcher pane
    // showing (no workspace open) AND the flyout open. PR context follows the
    // window being on screen — visible and not minimized — never keyboard focus: a reader left
    // beside another app's window stays readable and keeps its access lease renewed.
    void UpdateActivityVisibility() {
        if (DataContext is MainWindowViewModel vm) {
            vm.Activity.OnTabVisibleChanged(_activityOpen && IsVisible && vm.CurrentWorkspace is null);
            // Only a local workspace owns a PR reader; a remote host has none to foreground.
            var workspace = vm.CurrentWorkspace as WorkspaceViewModel;
            if (_foregroundWorkspace != workspace) _foregroundWorkspace?.PullRequests?.SetForeground(false);
            _foregroundWorkspace = workspace;
            workspace?.PullRequests?.SetForeground(IsVisible && WindowState != Avalonia.Controls.WindowState.Minimized);
        } else {
            _foregroundWorkspace?.PullRequests?.SetForeground(false);
            _foregroundWorkspace = null;
        }
    }
}

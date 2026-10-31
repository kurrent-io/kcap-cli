using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Wires TrayViewModel.MenuModel into a real TrayIcon/NativeMenu. Avalonia's Windows and Linux
/// backends never raise NativeMenu.NeedsUpdate, and Windows shows nothing at all for a menu with no
/// items, so there the menu is rebuilt on every model change and a left-click opens the main window.
/// On macOS it rebuilds lazily inside NeedsUpdate.
/// Those backends raise nothing when the menu opens either, so the pause state is refreshed on a
/// timer: a failed first read or a policy changed elsewhere would otherwise stay in the menu.
public sealed class TrayIconManager : IDisposable {
    readonly Application _app;
    readonly TrayIcon _trayIcon;
    readonly NativeMenu _menu = new();
    readonly TrayMenuBuilder _builder;
    readonly TrayMenuSync _sync = new();
    readonly IDisposable _subscription;
    readonly IDisposable? _pauseRefresh;

    static readonly TimeSpan PauseRefreshInterval = TimeSpan.FromSeconds(30);

    public TrayIconManager(Application app, TrayViewModel vm)
        : this(app, vm, rebuildEagerly: !Program.Host.TrayAnnouncesMenuOpen) { }

    public TrayIconManager(Application app, TrayViewModel vm, bool rebuildEagerly, TimeProvider? time = null) {
        _app = app;
        _builder = new TrayMenuBuilder(vm);

        _trayIcon = new TrayIcon { Menu = _menu, ToolTipText = "kcap" };
        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });

        if (rebuildEagerly) {
            _trayIcon.Command = vm.OpenMainWindowCommand;
            _trayIcon.Clicked += (_, _) => vm.RequestPauseRefresh();
            _pauseRefresh = (time ?? TimeProvider.System).CreateTimer(
                _ => vm.RequestPauseRefresh(), null, PauseRefreshInterval, PauseRefreshInterval);
        } else {
            // macOS status-item menus never raise NativeMenu.Opening, so this is also the pause refresh kick.
            _menu.NeedsUpdate += (_, _) => {
                vm.RequestPauseRefresh();
                _sync.OnNeedsUpdate(model => _builder.Rebuild(_menu, model));
            };
        }

        _subscription = vm.WhenAnyValue(x => x.MenuModel).Subscribe(model => {
            _trayIcon.Icon = TrayIconRenderer.Get(model.State, model.RunningCount);
            if (rebuildEagerly) {
                // Windows reads the tooltip as the icon's accessible name.
                _trayIcon.ToolTipText = model.ToolTip;
                _builder.Rebuild(_menu, model);
            } else _sync.OnModelChanged(model);
        });
    }

    public void Dispose() {
        _subscription.Dispose();
        _pauseRefresh?.Dispose();
        TrayIcon.SetIcons(_app, null);
        _trayIcon.Dispose();
    }
}

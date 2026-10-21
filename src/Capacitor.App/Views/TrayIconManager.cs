using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Wires TrayViewModel.MenuModel into a real TrayIcon/NativeMenu. Only macOS raises
/// NativeMenu.NeedsUpdate: the Windows backend and the Linux D-Bus exporter never do, and both show
/// nothing for a menu with no items. So off macOS the menu is rebuilt on every model change and a
/// left-click opens the main window; on macOS it rebuilds lazily inside NeedsUpdate.
/// Those backends raise nothing when the menu opens either, so off macOS the pause state is refreshed
/// on a timer: a failed first read or a policy changed elsewhere would otherwise stay in the menu.
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
        : this(app, vm, rebuildEagerly: !OperatingSystem.IsMacOS()) { }

    public TrayIconManager(Application app, TrayViewModel vm, bool rebuildEagerly, TimeProvider? time = null) {
        _app = app;
        _builder = new TrayMenuBuilder(vm);

        _trayIcon = new TrayIcon { Menu = _menu, ToolTipText = "Kurrent Capacitor" };
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
            if (rebuildEagerly) _builder.Rebuild(_menu, model);
            else _sync.OnModelChanged(model);
        });
    }

    public void Dispose() {
        _subscription.Dispose();
        _pauseRefresh?.Dispose();
        TrayIcon.SetIcons(_app, null);
        _trayIcon.Dispose();
    }
}

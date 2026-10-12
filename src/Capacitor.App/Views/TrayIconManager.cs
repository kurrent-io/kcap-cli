using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Wires TrayViewModel.MenuModel into a real TrayIcon/NativeMenu. Only Avalonia's macOS backend
/// raises NativeMenu.NeedsUpdate before a menu opens; the Windows and Linux backends never do, and
/// Windows shows nothing at all for a menu with no items. So macOS rebuilds lazily inside
/// NeedsUpdate, while everywhere else the menu is rebuilt on every model change and a left-click
/// opens the main window.
public sealed class TrayIconManager : IDisposable {
    readonly Application _app;
    readonly TrayIcon _trayIcon;
    readonly NativeMenu _menu = new();
    readonly TrayMenuBuilder _builder;
    readonly TrayMenuSync _sync = new();
    readonly IDisposable _subscription;

    public TrayIconManager(Application app, TrayViewModel vm)
        : this(app, vm, backendRaisesNeedsUpdate: OperatingSystem.IsMacOS()) { }

    public TrayIconManager(Application app, TrayViewModel vm, bool backendRaisesNeedsUpdate) {
        _app = app;
        _builder = new TrayMenuBuilder(vm);

        _trayIcon = new TrayIcon { Menu = _menu, ToolTipText = "Kurrent Capacitor" };
        TrayIcon.SetIcons(app, new TrayIcons { _trayIcon });

        if (backendRaisesNeedsUpdate) {
            // macOS status-item menus never raise NativeMenu.Opening, so this is also the pause refresh kick.
            _menu.NeedsUpdate += (_, _) => {
                vm.RequestPauseRefresh();
                _sync.OnNeedsUpdate(model => _builder.Rebuild(_menu, model));
            };
        } else {
            _trayIcon.Command = vm.OpenMainWindowCommand;
            _trayIcon.Clicked += (_, _) => vm.RequestPauseRefresh();
        }

        _subscription = vm.WhenAnyValue(x => x.MenuModel).Subscribe(model => {
            _trayIcon.Icon = TrayIconRenderer.Get(model.State, model.RunningCount);
            if (backendRaisesNeedsUpdate) _sync.OnModelChanged(model);
            else _builder.Rebuild(_menu, model);
        });
    }

    public void Dispose() {
        _subscription.Dispose();
        TrayIcon.SetIcons(_app, null);
        _trayIcon.Dispose();
    }
}

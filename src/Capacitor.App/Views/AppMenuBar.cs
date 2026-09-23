using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Commands;

namespace Capacitor.App.Views;

/// The Window and Help menus. Every window carries its own copy: on macOS Avalonia makes the key
/// window's menu the menu bar, and a menu set only on the Application loses its order there.
public sealed class AppMenuBar(IUrlOpener opener, Func<IReadOnlyList<Window>> windows, Func<Action?> showMainWindow) {
    public const string DocsUrl = "https://www.kurrent.io/docs/capacitor/";
    public const string ChangelogUrl = "https://github.com/kurrent-io/kcap-cli/releases";

    const string WindowMenuTitle = "Window";
    const string HelpMenuTitle = "Help";

    Action<FeedbackCategory>? _openFeedback;
    readonly List<ReportItems> _reportItems = [];

    public void SetFeedbackAction(Action<FeedbackCategory>? open) {
        _openFeedback = open;
        foreach (var items in _reportItems) {
            items.Bug.IsEnabled = open is not null;
            items.Feedback.IsEnabled = open is not null;
        }
    }

    /// Once per process: the class handler cannot be removed.
    public void Install() => Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Attach(window));

    public void Attach(Window window) {
        // Opened fires again each time a hidden window is shown, as the main window is from the tray.
        if (NativeMenu.GetMenu(window) is not null) return;

        NativeMenu.SetMenu(window, Build(window));
        // Avalonia swaps the main menu to the key window's own, so AppKit has to be pointed at this
        // window's copies again every time it becomes key.
        window.Activated += (_, _) => AppKitMenus.Adopt(WindowMenuTitle, HelpMenuTitle);
    }

    public NativeMenu Build(Window window) => new() {
        new NativeMenuItem(WindowMenuTitle) { Menu = BuildWindowMenu(window) },
        new NativeMenuItem(HelpMenuTitle) { Menu = BuildHelpMenu(window) },
    };

    NativeMenu BuildWindowMenu(Window window) {
        var menu = new NativeMenu {
            Item("Minimize", () => window.WindowState = WindowState.Minimized, new KeyGesture(Key.M, KeyModifiers.Meta)),
            Item("Zoom", () => Toggle(window, WindowState.Maximized), enabled: window.CanResize),
            // A fixed label: renaming an exported item makes Avalonia rebuild the native menu, which
            // would drop the AppKit registration until the window next becomes key.
            Item("Toggle Full Screen", () => Toggle(window, WindowState.FullScreen),
                new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta), window.CanResize),
            new NativeMenuItemSeparator(),
        };

        if (showMainWindow() is { } show) {
            menu.Add(Item("Kurrent Capacitor", show, new KeyGesture(Key.D0, KeyModifiers.Meta)));
            menu.Add(new NativeMenuItemSeparator());
        }

        menu.Add(Item("Bring All to Front", () => BringAllToFront(window)));
        return menu;
    }

    NativeMenu BuildHelpMenu(Window window) {
        // Resolved at click time: an item enabled by a later SetFeedbackAction must invoke that action.
        var bug      = Item("Report a Bug…",  () => _openFeedback?.Invoke(FeedbackCategory.Bug),      enabled: _openFeedback is not null);
        var feedback = Item("Send Feedback…", () => _openFeedback?.Invoke(FeedbackCategory.Feedback), enabled: _openFeedback is not null);
        var items    = new ReportItems(bug, feedback);
        _reportItems.Add(items);
        window.Closed += (_, _) => _reportItems.Remove(items);

        return new NativeMenu {
            Item("Kurrent Capacitor Documentation", () => LinkPolicy.Open(opener, DocsUrl)),
            Item("Changelog", () => LinkPolicy.Open(opener, ChangelogUrl)),
            new NativeMenuItemSeparator(),
            bug,
            feedback,
        };
    }

    static void Toggle(Window window, WindowState state) =>
        window.WindowState = window.WindowState == state ? WindowState.Normal : state;

    void BringAllToFront(Window invoking) {
        foreach (var window in windows()) {
            if (window != invoking && window.IsVisible && window.WindowState != WindowState.Minimized) window.Activate();
        }

        invoking.Activate();
    }

    static NativeMenuItem Item(string header, Action action, KeyGesture? gesture = null, bool enabled = true) {
        var item = new NativeMenuItem(header) { Gesture = gesture, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }
}

using System.Windows.Input;
using Avalonia.Input;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// A UI scale shortcut on the main window: ⌘ on macOS, Ctrl elsewhere. A Ctrl shortcut yields to a
/// focused terminal by being unable to execute, so the key stays unhandled and the shell receives it.
sealed class ZoomShortcut(MainWindow window, Func<double, double> next) : ICommand {
    static readonly KeyModifiers Modifier = RefreshShortcut.UsesControl ? KeyModifiers.Control : KeyModifiers.Meta;

    /// Larger on = (also + with Shift) and keypad +; smaller on - and keypad -; default on 0.
    public static IEnumerable<KeyBinding> For(MainWindow window) {
        var larger  = new ZoomShortcut(window, UiScale.Larger);
        var smaller = new ZoomShortcut(window, UiScale.Smaller);
        yield return Bind(Key.OemPlus, Modifier, larger);
        yield return Bind(Key.OemPlus, Modifier | KeyModifiers.Shift, larger);
        yield return Bind(Key.Add, Modifier, larger);
        yield return Bind(Key.OemMinus, Modifier, smaller);
        yield return Bind(Key.Subtract, Modifier, smaller);
        yield return Bind(Key.D0, Modifier, new ZoomShortcut(window, _ => UiScale.Default));
    }

    static KeyBinding Bind(Key key, KeyModifiers modifiers, ICommand command) =>
        new() { Gesture = new KeyGesture(key, modifiers), Command = command };

    public bool CanExecute(object? parameter) =>
        window.ContentScale is not null && !(RefreshShortcut.UsesControl && TerminalFocus.Has(window));

    public void Execute(object? parameter) {
        if (!CanExecute(parameter) || window.ContentScale is not { } setting) return;
        _ = setting.SetAsync(next(setting.Current));
    }

    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

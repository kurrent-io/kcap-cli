using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using SvcSystems.UI.Terminal;

namespace Capacitor.App.Views;

/// Ctrl+R on Linux and Windows. CanExecute is false while focus is inside the terminal, so the
/// key stays unhandled and the terminal sends it. The menu and Ctrl+Shift+R use the real command.
sealed class RefreshUnlessTerminalFocused(TopLevel window) : ICommand {
    public bool CanExecute(object? parameter) =>
        !FocusedInTerminal() && Inner?.CanExecute(parameter) == true;

    public void Execute(object? parameter) {
        if (FocusedInTerminal() || Inner is not { } inner || !inner.CanExecute(parameter)) return;
        inner.Execute(parameter);
    }

    // Read on the key press. Nothing can cache this: focus is not a command input.
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    ICommand? Inner => (window.DataContext as MainWindowViewModel)?.RefreshWorkCommand;

    bool FocusedInTerminal() {
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        while (focused is not null) {
            if (focused is TerminalControl) return true;
            focused = focused.GetVisualParent();
        }
        return false;
    }
}

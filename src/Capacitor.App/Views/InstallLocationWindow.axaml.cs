using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

public partial class InstallLocationWindow : Window {
    public InstallLocationWindow() => InitializeComponent();

    internal InstallLocationWindow(string where, Func<Task<MoveOutcome>> move, Action quit) : this() {
        Explanation.Text = $"{where} Its command-line tool and background service need a permanent location.";

        MoveButton.Click += async (_, _) => {
            SetMoving(true);
            try {
                var outcome = await move();
                if (!outcome.Moved) ShowError(outcome.Error);
            } catch (Exception ex) {
                ShowError(ex.Message);
            }
        };
        QuitButton.Click += (_, _) => quit();
        // The titlebar close / Cmd+W leaves no other path to shutdown — OnExplicitShutdown means
        // Avalonia never ends the process on its own just because the last window closed.
        Closed += (_, _) => quit();
    }

    void SetMoving(bool moving) {
        MoveButton.IsEnabled = !moving;
        MoveButton.Content = moving ? "Moving…" : "Move to Applications";
        if (moving) ErrorPanel.IsVisible = false;
    }

    void ShowError(string? message) {
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
        SetMoving(false);
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}

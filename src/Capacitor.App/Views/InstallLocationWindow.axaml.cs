using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

public partial class InstallLocationWindow : Window {
    bool _moving;
    string _actionLabel = "Install and open";
    bool _canMove = true;
    public InstallLocationWindow() => InitializeComponent();

    internal InstallLocationWindow(string where, Func<Task<MoveOutcome>> move, Action quit,
            ApplicationsInstallPlan? plan = null, IReadOnlyList<string>? destinations = null,
            Func<string, ApplicationsInstallPlan>? selectDestination = null) : this() {
        void ShowPlan(ApplicationsInstallPlan? selected) {
            Heading.Text = selected?.Title ?? "Install kcap";
            _actionLabel = selected?.ButtonLabel ?? "Install and open";
            MoveButton.Content = _actionLabel;
            var detail = selected?.Detail ?? "Its command-line tool and background service need a permanent location.";
            Explanation.Text = selected is null ? $"{where} {detail}" : detail;
            LocationPanel.IsVisible = selected is not null && selected.Action != ApplicationsInstallAction.Blocked
                && destinations is not { Count: > 1 };
            LocationPath.Text = selected?.Target;
            InstallArrow.IsVisible = InstallFolder.IsVisible = selected is null || selected.Action == ApplicationsInstallAction.Install;
            UpdateBadge.IsVisible = selected?.Action == ApplicationsInstallAction.Update;
            ReadyBadge.IsVisible = selected?.Action == ApplicationsInstallAction.OpenInstalled;
            ConflictBadge.IsVisible = selected?.Action == ApplicationsInstallAction.Blocked;
            _canMove = selected?.Action != ApplicationsInstallAction.Blocked;
            MoveButton.IsEnabled = _canMove;
        }
        ShowPlan(plan);
        if (destinations is { Count: > 1 }) {
            DestinationPanel.IsVisible = true;
            DestinationPicker.ItemsSource = destinations;
            Heading.Text = "Choose an install location";
            Explanation.Text = "Choose which Applications folder kcap should use. Unrelated apps will stay untouched.";
            _canMove = false;
            MoveButton.IsEnabled = false;
            DestinationPicker.SelectionChanged += (_, _) => {
                if (DestinationPicker.SelectedItem is string directory && selectDestination is not null) {
                    ShowPlan(selectDestination(directory));
                    ErrorPanel.IsVisible = false;
                }
            };
        }

        MoveButton.Click += async (_, _) => {
            SetMoving(true);
            try {
                var outcome = await move();
                if (!outcome.Moved) ShowError(outcome.Error);
            } catch (Exception ex) {
                ShowError(ex.Message);
            } finally {
                SetMoving(false);
            }
        };
        QuitButton.Click += (_, _) => quit();
        Closing += (_, e) => e.Cancel = _moving && e.CloseReason == WindowCloseReason.WindowClosing;
        Closed += (_, _) => quit();
    }

    void SetMoving(bool moving) {
        _moving = moving;
        MoveButton.IsEnabled = !moving && _canMove;
        DestinationPicker.IsEnabled = !moving;
        QuitButton.IsEnabled = !moving;
        MoveButton.Content = moving ? "Preparing kcap…" : _actionLabel;
        if (moving) ErrorPanel.IsVisible = false;
    }

    void ShowError(string? message) {
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
        SetMoving(false);
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}

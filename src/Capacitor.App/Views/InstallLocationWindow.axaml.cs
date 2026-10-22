using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Views;

public partial class InstallLocationWindow : Window {
    bool _moving;
    string _actionLabel = "Install and open";
    bool _canMove = true;
    public InstallLocationWindow() => InitializeComponent();

    internal InstallLocationWindow(string where, Func<Task<MoveOutcome>> move, Action quit,
            ApplicationsInstallPlan? plan = null, IReadOnlyList<string>? destinations = null,
            Func<string, ApplicationsInstallPlan>? selectDestination = null,
            string? defaultDestination = null, string? userApplicationsDirectory = null) : this() {
        var selectedDirectory = defaultDestination;
        void ShowPlan(ApplicationsInstallPlan? selected) {
            Heading.Text = selected?.Title ?? "Install kcap";
            _actionLabel = selected?.ButtonLabel ?? "Install and open";
            MoveButton.Content = _actionLabel;
            var detail = selected?.Detail ?? "Its command-line tool and background service need a permanent location.";
            Explanation.Text = selected is null ? $"{where} {detail}" : detail;
            InstallBadge.IsVisible = selected is null || selected.Action == ApplicationsInstallAction.Install;
            UpdateBadge.IsVisible = selected?.Action == ApplicationsInstallAction.Update;
            ReadyBadge.IsVisible = selected?.Action == ApplicationsInstallAction.OpenInstalled;
            ConflictBadge.IsVisible = selected?.Action == ApplicationsInstallAction.Blocked;
            _canMove = selected?.Action != ApplicationsInstallAction.Blocked;
            MoveButton.IsEnabled = _canMove;
        }
        ShowPlan(plan);
        if (destinations is { Count: > 1 }) {
            DestinationPanel.IsVisible = true;
            DestinationPicker.ItemsSource = destinations.Select(directory => new ComboBoxItem {
                Content = directory == userApplicationsDirectory
                    ? "Install for me only"
                    : directory == "/Applications" ? "Install for all users" : directory,
                Tag = directory,
            }).ToArray();
            if (defaultDestination is null) {
                Heading.Text = "Choose an install location";
                Explanation.Text = "Choose which Applications folder kcap should use. Unrelated apps will stay untouched.";
                _canMove = false;
                MoveButton.IsEnabled = false;
            }
            DestinationPicker.SelectionChanged += (_, _) => {
                if (DestinationPicker.SelectedItem is ComboBoxItem { Tag: string directory } && selectDestination is not null) {
                    selectedDirectory = directory;
                    ShowPlan(selectDestination(directory));
                    ErrorPanel.IsVisible = false;
                }
            };
            DestinationPicker.SelectedIndex = destinations.ToList().IndexOf(defaultDestination!);
        }

        MoveButton.Click += async (_, _) => {
            SetMoving(true);
            try {
                var outcome = await move();
                if (!outcome.Moved) ShowError(outcome.PermissionDenied && userApplicationsDirectory is not null
                        && selectedDirectory != userApplicationsDirectory
                    ? $"kcap cannot write to {selectedDirectory}. Choose “Install for me only” and try again."
                    : outcome.Error);
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

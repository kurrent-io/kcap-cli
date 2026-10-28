using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Views;

public partial class InstallLocationWindow : Window {
    bool _moving;
    string _actionLabel = "Install and open";
    bool _canMove = true;
    readonly Func<Task>? _startMove;
    public InstallLocationWindow() => InitializeComponent();

    internal InstallLocationWindow(string where, Func<Task<MoveOutcome>> move, Action quit,
            ApplicationsInstallPlan? plan = null, IReadOnlyList<string>? destinations = null,
            Func<string, ApplicationsInstallPlan>? selectDestination = null,
            string? userApplicationsDirectory = null, string? quitLabel = null, bool keepOne = false) : this() {
        if (quitLabel is not null) QuitButton.Content = quitLabel;
        var selectedDirectory = Path.GetDirectoryName(plan?.Target);
        var shown = plan;
        void ShowPlan(ApplicationsInstallPlan? selected) {
            shown = selected;
            Heading.Text = selected?.Title ?? "Install kcap";
            _actionLabel = selected?.ButtonLabel ?? "Install and open";
            MoveButton.Content = _actionLabel;
            var detail = selected?.Detail ?? "Its command-line tool and background service need a permanent location.";
            Explanation.Text = selected is null ? $"{where} {detail}" : detail;
            InstallBadge.IsVisible = selected is null || selected.Action == ApplicationsInstallAction.Install && !selected.Migrates;
            UpdateBadge.IsVisible = selected?.Action == ApplicationsInstallAction.Update || selected?.Migrates == true;
            ReadyBadge.IsVisible = selected is { Action: ApplicationsInstallAction.OpenInstalled, Migrates: false };
            ConflictBadge.IsVisible = selected?.Action == ApplicationsInstallAction.Blocked;
            _canMove = selected?.Action != ApplicationsInstallAction.Blocked;
            // Blocked by something in the way, the action shows it in Finder; an unverifiable copy has nothing to offer.
            var revealable = selected is { Action: ApplicationsInstallAction.Blocked, SourceVersion: not null } && Path.Exists(selected.Target);
            MoveButton.IsVisible = _canMove || revealable;
            MoveButton.IsEnabled = _canMove || revealable;
        }
        ShowPlan(plan);
        if (destinations is { Count: > 1 }) {
            DestinationPanel.IsVisible = true;
            DestinationPicker.ItemsSource = destinations.Select(directory => new ComboBoxItem {
                Content = keepOne ? $"Keep the copy in {directory}"
                    : directory == userApplicationsDirectory ? "Install for me only"
                    : directory == "/Applications" ? "Install for all users" : directory,
                Tag = directory,
            }).ToArray();
            Heading.Text = keepOne ? "kcap is installed twice" : "Choose an install location";
            if (keepOne) {
                DestinationLabel.Text = "Copy to keep";
                DestinationPicker.PlaceholderText = "Select a copy…";
            }
            Explanation.Text = keepOne
                ? "Choose the copy to keep. The other one is removed, and the daemon and the terminal command use the copy you keep."
                : "Choose which Applications folder kcap should use. Unrelated apps will stay untouched.";
            _canMove = false;
            MoveButton.IsEnabled = false;
            DestinationPicker.SelectionChanged += (_, _) => {
                if (DestinationPicker.SelectedItem is ComboBoxItem { Tag: string directory } && selectDestination is not null) {
                    selectedDirectory = directory;
                    ShowPlan(selectDestination(directory));
                    ErrorPanel.IsVisible = false;
                }
            };
        }

        void OfferUserInstall(string userApps) {
            var denied = selectedDirectory;
            selectedDirectory = userApps;
            if (DestinationPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, userApps)) is { } item)
                DestinationPicker.SelectedItem = item;
            else
                ShowPlan(selectDestination!(userApps));
            if (shown?.Action == ApplicationsInstallAction.Install) _actionLabel = "Install for me only";
            ShowError($"kcap cannot write to {denied}. You can install it in {userApps} for your account only.");
        }

        _startMove = async () => {
            SetMoving(true);
            try {
                var outcome = await move();
                if (outcome.Moved) return;
                if (outcome.PermissionDenied && userApplicationsDirectory is not null && selectDestination is not null
                        && selectedDirectory != userApplicationsDirectory)
                    OfferUserInstall(userApplicationsDirectory);
                else
                    ShowError(outcome.Error);
            } catch (Exception ex) {
                ShowError(ex.Message);
            } finally {
                SetMoving(false);
            }
        };
        MoveButton.Click += (_, _) => {
            if (shown is { Action: ApplicationsInstallAction.Blocked } blocked) Reveal(blocked.Target);
            else StartMove();
        };
        // Coming back from Finder re-checks the folder, so a cleared conflict turns straight back into an install.
        Activated += (_, _) => {
            if (!_moving && shown?.Action == ApplicationsInstallAction.Blocked && selectDestination is not null && selectedDirectory is not null
                    && (!DestinationPanel.IsVisible || DestinationPicker.SelectedItem is not null))
                ShowPlan(selectDestination(selectedDirectory));
        };
        QuitButton.Click += (_, _) => quit();
        Closing += (_, e) => e.Cancel = _moving && e.CloseReason == WindowCloseReason.WindowClosing;
        Closed += (_, _) => quit();
    }

    /// Runs the window's action as its button would; ignored while one runs or none is possible.
    internal void StartMove() {
        if (!_moving && _canMove && _startMove is { } start) _ = start();
    }

    static void Reveal(string path) {
        try {
            Process.Start(new ProcessStartInfo("open", ["-R", path]) { UseShellExecute = false })?.Dispose();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not show {path} in Finder: {ex.Message}");
        }
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

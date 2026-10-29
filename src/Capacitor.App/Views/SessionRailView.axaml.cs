using System.ComponentModel;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The session rail. DataContext is the window's own MainWindowViewModel, inherited — this view
/// never builds or assigns one.
public partial class SessionRailView : UserControl {
    public SessionRailView() => InitializeComponent();

    // The rail's 44px chrome row IS the title bar on this surface — see WindowChrome.
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    public event EventHandler? CollapseRequested;

    void OnCollapseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);

    // Button.OnClick raises ClickEvent before reading Command, and Flyout.Hide detaches the popup
    // parent and clears inherited DataContext on its rows, so capture and run Command here.
    void OnHelpFlyoutItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailHelpButton, sender, e);

    void OnGroupPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailGroupButton, sender, e);

    void OnSortPickerItemClick(object? sender, RoutedEventArgs e) => RunFlyoutItem(RailSortButton, sender, e);

    static void RunFlyoutItem(Button anchor, object? sender, RoutedEventArgs e) {
        if (sender is not Button { Command: { } command, CommandParameter: var parameter }
            || !command.CanExecute(parameter)) return;
        anchor.Flyout?.Hide();
        command.Execute(parameter);
        e.Handled = true;
    }

    AboutWindow? _about;

    void OnAboutClick(object? sender, RoutedEventArgs e) {
        RailHelpButton.Flyout?.Hide();
        if (_about is { } open) {
            open.Activate();
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        _about = new AboutWindow { DataContext = DataContext };
        _about.Closed += (_, _) => _about = null;
        _about.Show(owner);
    }

    void OnPullRequestClick(object? sender, RoutedEventArgs e) {
        if (sender is not Button { DataContext: RailSessionViewModel vm } anchor
            || !vm.HasMultiplePullRequests || vm.PullRequest is not { } summary) return;

        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(6) };
        var flyout = new Flyout {
            Placement = PlacementMode.BottomEdgeAlignedRight, VerticalOffset = 6,
            Content = new ScrollViewer { Content = rows, MaxHeight = 400, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        foreach (var item in summary.MenuItems) {
            var body = new StackPanel { Spacing = 3 };
            body.Children.Add(new TextBlock {
                Text = item.Label, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (IBrush)this.FindResource("KcapTextBrush")!,
            });
            body.Children.Add(new TextBlock {
                Text = item.Detail, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (IBrush)this.FindResource("KcapMutedBrush")!,
            });
            var option = new Button {
                Content = body, DataContext = item, MinWidth = 240, MaxWidth = 360, Padding = new Thickness(10, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            option.Classes.Add("kcapGhost");
            AutomationProperties.SetName(option, $"Open {item.Detail}: {item.Label}");
            ToolTip.SetTip(option, $"{item.Label}\n{item.Detail}");
            option.Click += (_, _) => {
                flyout.Hide();
                vm.OpenPullRequestChoiceCommand.Execute(item).Subscribe();
            };
            rows.Children.Add(option);
        }
        // Close when the access-aware summary changes, including expiry or removal.
        PropertyChangedEventHandler changed = (_, args) => {
            if (args.PropertyName == nameof(RailSessionViewModel.PullRequest)) flyout.Hide();
        };
        vm.PropertyChanged += changed;
        flyout.Closed += (_, _) => vm.PropertyChanged -= changed;
        flyout.Opened += (_, _) => rows.Children.OfType<Button>().FirstOrDefault()?.Focus();
        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        flyout.ShowAt(anchor);
        e.Handled = true;
    }
}

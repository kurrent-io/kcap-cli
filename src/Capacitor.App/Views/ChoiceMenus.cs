using Lucide.Avalonia;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// Chrome shared by the launcher's pickers: a kcapPanel Flyout of ghost rows, the choice marked
/// by SelectionCheck in every menu. The extension members resolve theme brushes from the control
/// that hosts the menu.
internal static class ChoiceMenus {
    /// Left-aligned: a menu wider than its chip, centred, overhangs the composer's left edge.
    internal static Flyout PanelFlyout(Control content, double minWidth) {
        var host = new Border { Child = content, MinWidth = minWidth };
        var flyout = new Flyout {
            Placement = PlacementMode.BottomEdgeAlignedLeft, Content = host,
            // A few pixels of air between the chip and the panel — flush looks glued on.
            VerticalOffset = 6,
        };
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        return flyout;
    }

    internal static Button ChoiceRow(this Control scope, string label, bool selected, Action pick) {
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        body.Children.Add(new TextBlock {
            Text = label, FontSize = 13.5, Foreground = scope.Brush("KcapTextBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        var check = scope.SelectionCheck(selected);
        Grid.SetColumn(check, 1);
        body.Children.Add(check);
        var row = ChoiceButton(body, pick);
        AutomationProperties.SetName(row, ChoiceName(label, selected));
        return row;
    }

    /// The check mark is drawn only, so the selection has to be spoken in the row's name.
    internal static string ChoiceName(string? label, bool selected) => selected ? $"{label}, selected" : label ?? "";

    internal static AppIcon SelectionCheck(this Control scope, bool selected) => new() {
        Kind = LucideIconKind.Check, Size = 14, IsVisible = selected,
        Foreground = scope.Brush("KcapInfoBrush"), Margin = new Thickness(12, 0, 0, 0),
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    internal static Button ChoiceButton(object content, Action pick) {
        var row = new Button {
            Content = content,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            // Stretch: left-aligned content stays content-sized and never pushes a trailing pill
            // to the row's right edge (repo list badges).
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
        };
        row.Classes.Add("kcapGhost");
        row.Click += (_, _) => pick();
        return row;
    }

    internal static Border ChoiceSeparator(this Control scope) => new() {
        Height = 1, Margin = new Thickness(8, 4),
        Background = scope.Brush("KcapBorderBrush"),
    };

    internal static IBrush Brush(this Control scope, string key) => (IBrush)scope.FindResource(key)!;
}

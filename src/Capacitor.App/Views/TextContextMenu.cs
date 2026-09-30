using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Capacitor.App.Views;

/// The right-click menu on text: a `kcapPanel` of ghost rows, where Fluent's own is a MenuFlyout
/// in MenuItem chrome. Each flyout is shared the way Fluent shares its own, so a row acts on
/// whichever control opened it.
public static class TextContextMenu {
    sealed record Row(string Label, Func<Control, bool> CanRun, Action<Control> Run);

    public static FlyoutBase ForTextBox { get; } = Build<TextBox>(
        new("Cut", c => ((TextBox)c).CanCut, c => ((TextBox)c).Cut()),
        new("Copy", c => ((TextBox)c).CanCopy, c => ((TextBox)c).Copy()),
        new("Paste", c => ((TextBox)c).CanPaste, c => ((TextBox)c).Paste()),
        new("Select all", c => !string.IsNullOrEmpty(((TextBox)c).Text), c => ((TextBox)c).SelectAll()));

    public static FlyoutBase ForSelectableTextBlock { get; } = Build<SelectableTextBlock>(
        new("Copy", c => ((SelectableTextBlock)c).CanCopy, c => ((SelectableTextBlock)c).Copy()),
        new("Select all", c => !string.IsNullOrEmpty(((SelectableTextBlock)c).Text), c => ((SelectableTextBlock)c).SelectAll()));

    public static FlyoutBase ForMarkdown { get; } = Build<MarkdownView>(
        new("Copy", c => ((MarkdownView)c).HasSelection, c => _ = ((MarkdownView)c).CopySelectionAsync()),
        new("Select all", _ => true, c => ((MarkdownView)c).SelectAll()));

    static Flyout Build<T>(params Row[] rows) where T : Control {
        var flyout = new Flyout();
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        var panel = new StackPanel { Spacing = 2, Margin = new(6), MinWidth = 160 };
        var buttons = new List<(Button Button, Row Row)>();
        foreach (var row in rows) {
            var button = new Button {
                Content = row.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new(10, 6),
                FontSize = 13.5,
            };
            button.Classes.Add("kcapGhost");
            button.Click += (_, _) => {
                var target = flyout.Target;
                flyout.Hide();
                if (target is T) row.Run(target);
            };
            panel.Children.Add(button);
            buttons.Add((button, row));
        }
        flyout.Content = panel;
        flyout.Opening += (_, _) => {
            foreach (var (button, row) in buttons) button.IsEnabled = flyout.Target is T target && row.CanRun(target);
        };
        return flyout;
    }
}

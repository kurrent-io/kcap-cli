using Avalonia.Controls;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// Sizes a session view's work-context column from its window's <see cref="PaneLayout"/>. Outside a
/// <see cref="MainWindow"/> the column keeps its minimum width and neither button shows, since
/// nothing could bring a hidden pane back.
public static class ContextPaneSizing {
    public static void Attach(Control view, ColumnDefinition column, Control pane, WorkContextView content,
            GridSplitter splitter, Button showButton) {
        PaneLayout? panes = null;

        void Apply() {
            var collapsed = panes?.ContextCollapsed ?? false;
            column.MinWidth = panes is null ? PaneLayout.ContextMinWidth : 0;
            column.Width = new GridLength(collapsed ? 0 : panes?.ContextWidth ?? PaneLayout.ContextMinWidth);
            pane.IsVisible = !collapsed;
            splitter.IsVisible = !collapsed;
            showButton.IsVisible = collapsed;
            content.HideButton.IsVisible = panes is not null;
        }

        void OnChanged(object? sender, EventArgs e) => Apply();

        view.AttachedToVisualTree += (_, _) => {
            panes = (TopLevel.GetTopLevel(view) as MainWindow)?.Panes;
            if (panes is not null) panes.Changed += OnChanged;
            Apply();
        };
        view.DetachedFromVisualTree += (_, _) => {
            if (panes is not null) panes.Changed -= OnChanged;
            panes = null;
        };
        view.SizeChanged += (_, e) => column.MaxWidth = e.NewSize.Width * PaneLayout.MaxShare;
        splitter.DragDelta += (_, _) =>
            pane.Opacity = panes is not null && PaneLayout.HidesAt(column.ActualWidth, PaneLayout.ContextMinWidth)
                ? PaneLayout.HidingOpacity
                : 1;
        // Released below the minimum, the pane hides and reopens at the minimum.
        splitter.DragCompleted += (_, _) => {
            pane.Opacity = 1;
            if (panes is null) return;
            var hides = PaneLayout.HidesAt(column.ActualWidth, PaneLayout.ContextMinWidth);
            panes.ContextWidth = hides ? PaneLayout.ContextMinWidth : column.ActualWidth;
            if (hides) panes.ContextCollapsed = true;
        };
        content.HideRequested += (_, _) => {
            if (panes is not null) panes.ContextCollapsed = true;
        };
        showButton.Click += (_, _) => {
            if (panes is not null) panes.ContextCollapsed = false;
        };
        Apply();
    }
}

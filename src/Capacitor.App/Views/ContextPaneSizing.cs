using Avalonia.Controls;
using Capacitor.App.Services;
using Lucide.Avalonia;

namespace Capacitor.App.Views;

/// Sizes a session view's work-context column from its window's <see cref="PaneLayout"/>. Outside a
/// <see cref="MainWindow"/> the column keeps its minimum width and the toggle is hidden.
public static class ContextPaneSizing {
    public static void Attach(Control view, ColumnDefinition column, Control pane, GridSplitter splitter, Button toggle, AppIcon toggleIcon) {
        PaneLayout? panes = null;

        void Apply() {
            toggle.IsVisible = panes is not null;
            var collapsed = panes?.ContextCollapsed ?? false;
            column.MinWidth = collapsed ? 0 : PaneLayout.ContextMinWidth;
            column.Width = new GridLength(collapsed ? 0 : panes?.ContextWidth ?? PaneLayout.ContextMinWidth);
            pane.IsVisible = !collapsed;
            splitter.IsVisible = !collapsed;
            toggleIcon.Kind = collapsed ? LucideIconKind.PanelRightOpen : LucideIconKind.PanelRightClose;
            var label = collapsed ? "Show details" : "Hide details";
            ToolTip.SetTip(toggle, label);
            Avalonia.Automation.AutomationProperties.SetName(toggle, label);
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
        splitter.DragCompleted += (_, _) => {
            if (panes is not null) panes.ContextWidth = column.ActualWidth;
        };
        toggle.Click += (_, _) => {
            if (panes is not null) panes.ContextCollapsed = !panes.ContextCollapsed;
        };
        Apply();
    }
}

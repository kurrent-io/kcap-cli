using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SvcSystems.UI.Terminal;

namespace Capacitor.App.Views;

public sealed class RemoteTerminalControl : TerminalControl {
    public static readonly StyledProperty<int> SourceColumnsProperty =
        AvaloniaProperty.Register<RemoteTerminalControl, int>(nameof(SourceColumns), 80);
    public static readonly StyledProperty<int> SourceRowsProperty =
        AvaloniaProperty.Register<RemoteTerminalControl, int>(nameof(SourceRows), 24);

    public int SourceColumns { get => GetValue(SourceColumnsProperty); set => SetValue(SourceColumnsProperty, value); }
    public int SourceRows { get => GetValue(SourceRowsProperty); set => SetValue(SourceRowsProperty, value); }
    public event Action<int, int>? ViewportChanged;

    Size _cell;
    Size _viewport;
    public double CellHeight => _cell.Height;

    WheelStepAccumulator _wheel;
    readonly ScrollBar? _historyScrollBar;
    Action? _modelRefresh;

    public void SendWheel(double delta, Point position) {
        if (Model is not { } model || _cell.Width <= 0 || _cell.Height <= 0 || delta == 0) return;
        var steps = _wheel.Take(delta, 6);
        var col = Math.Clamp((int)(position.X / _cell.Width), 0, model.Terminal.Cols - 1);
        var row = Math.Clamp((int)(position.Y / _cell.Height), 0, model.Terminal.Rows - 1);
        var up = steps > 0;
        for (var i = 0; i < Math.Abs(steps); i++) {
            var sequence = model.Terminal.Engine.GenerateMouseEvent(
                up ? XTerm.Input.MouseButton.WheelUp : XTerm.Input.MouseButton.WheelDown,
                col, row, up ? XTerm.Input.MouseEventType.WheelUp : XTerm.Input.MouseEventType.WheelDown,
                XTerm.Input.KeyModifiers.None);
            model.Send(sequence);
        }
    }

    public RemoteTerminalControl() {
        ClipToBounds = true;
        UseLayoutRounding = false;
        var bar = Children.OfType<ScrollBar>().FirstOrDefault();
        if (ColumnDefinitions.Count == 2 && RowDefinitions.Count == 0 && bar is not null && GetColumn(bar) == 1)
            _historyScrollBar = bar;
        UpdateSourceLayout();
        UpdateHistoryScrollBar();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        // Restore the base callback so its model replacement detaches the old model normally.
        if (change.Property == ModelProperty && change.OldValue is TerminalControlModel oldModel
            && oldModel.UpdateUI == RefreshRemoteModel)
            oldModel.UpdateUI = _modelRefresh;
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty) {
            _wheel = default;
            _modelRefresh = Model?.UpdateUI;
            if (Model is { } model) model.UpdateUI = RefreshRemoteModel;
            UpdateHistoryScrollBar();
        }
        if (change.Property == SourceColumnsProperty || change.Property == SourceRowsProperty
            || change.Property == FontFamilyProperty || change.Property == FontSizeProperty)
            UpdateSourceLayout();
    }

    void RefreshRemoteModel() {
        _modelRefresh?.Invoke();
        if (!(Model?.IsMouseModeActive ?? false)) _wheel = default;
        UpdateHistoryScrollBar();
    }

    void UpdateHistoryScrollBar() {
        if (_historyScrollBar is null || ColumnDefinitions.Count != 2) return;
        var sourceOwnsHistory = Model?.IsMouseModeActive ?? false;
        var hidden = sourceOwnsHistory || (Model?.Terminal.IsAlternateBufferActive ?? false);
        _historyScrollBar.Visibility = hidden ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        _historyScrollBar.IsHitTestVisible = !hidden;
        ColumnDefinitions[1].MaxWidth = hidden ? 0 : double.PositiveInfinity;
    }

    void UpdateSourceLayout() {
        if (_historyScrollBar is null || ColumnDefinitions.Count != 2 || RowDefinitions.Count > 1) return;
        var typeface = new Typeface(FontFamily);
        if (!FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs)) {
            typeface = new Typeface(Avalonia.Media.FontFamily.Default);
            if (!FontManager.Current.TryGetGlyphTypeface(typeface, out glyphs)) return;
        }
        var shaped = TextShaper.Current.ShapeText("a", new TextShaperOptions(glyphs, FontSize));
        using var run = new ShapedTextRun(shaped, new GenericTextRunProperties(typeface, FontSize));
        _cell = run.Size;
        // The control resizes its buffer from its first grid cell. Keep that cell at the source size.
        // A fractional cell prevents integer division from rounding the dimensions down.
        ColumnDefinitions[0].Width = new GridLength((SourceColumns + 0.01) * _cell.Width);
        RowDefinitions = [new RowDefinition((SourceRows + 0.01) * _cell.Height, GridUnitType.Pixel)];
        ReportViewport();
    }

    public void SetViewport(Size size) {
        _viewport = size;
        ReportViewport();
    }

    public void ReportViewport() {
        var size = _viewport;
        if (_cell.Width <= 0 || _cell.Height <= 0 || size.Width <= 0 || size.Height <= 0) return;
        var scrollbar = _historyScrollBar is not null && ColumnDefinitions.Count >= 2
            ? ColumnDefinitions[1].ActualWidth : 0;
        ViewportChanged?.Invoke(Math.Max(1, (int)((size.Width - scrollbar) / _cell.Width)),
            Math.Max(1, (int)(size.Height / _cell.Height)));
    }
}

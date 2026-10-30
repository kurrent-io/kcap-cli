using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

public partial class UpdateFinishWindow : ReactiveWindow<UpdateFinishViewModel> {
    public UpdateFinishWindow() {
        InitializeComponent();
        Opened += (_, _) => FitToScreen();
        ScalingChanged += (_, _) => FitToScreen();
    }

    void FitToScreen() {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        var area = screen.WorkingArea;
        MaxHeight = Math.Min(560, area.Height / screen.Scaling - 24);
        Height = Math.Min(Height, MaxHeight);
        Position = new PixelPoint(Position.X,
            Math.Max(area.Y, Math.Min(Position.Y, area.Bottom - (int)(Height * screen.Scaling))));
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);

    void OnDetails(object? sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: Control detail } button) return;
        detail.IsVisible = !detail.IsVisible;
        button.Content = detail.IsVisible ? "Hide details" : "Details";
    }
}

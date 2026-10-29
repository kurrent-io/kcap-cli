using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class UpdateCheckWindow : Window {
    public UpdateCheckWindow() => InitializeComponent();

    void OnHide(object? sender, RoutedEventArgs e) => Close();
    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}

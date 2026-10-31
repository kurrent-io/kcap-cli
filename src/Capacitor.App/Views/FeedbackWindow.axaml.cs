using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

public partial class FeedbackWindow : Window {
    public FeedbackWindow() => InitializeComponent();

    void OnCancel(object? sender, RoutedEventArgs e) => Close();

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}

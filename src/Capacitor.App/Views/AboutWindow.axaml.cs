using Avalonia.Controls;
using Avalonia.Input;

namespace Capacitor.App.Views;

/// The About panel macOS draws for the app menu; Windows has no native one to call.
public partial class AboutWindow : Window {
    public AboutWindow() => InitializeComponent();

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}

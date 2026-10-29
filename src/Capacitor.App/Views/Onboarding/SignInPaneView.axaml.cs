using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views.Onboarding;

public partial class SignInPaneView : UserControl {
    public SignInPaneView() => InitializeComponent();

    async void OnCopySignInUrlClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not SignInStepViewModel { BrowserUrl: { Length: > 0 } url }) return;
        await CopyAsync(url, sender as Button);
    }

    async void OnCopyDeviceCodeClick(object? sender, RoutedEventArgs e) {
        if (DataContext is SignInStepViewModel { DeviceCode: { Length: > 0 } code })
            await CopyAsync(code, sender as Button);
    }

    async Task CopyAsync(string text, Button? button) {
        try {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(text);
            if (button is not null) button.Content = "Copied";
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: clipboard unavailable: {ex.Message}");
            if (button is not null) button.Content = "Could not copy — try again";
        }
    }
}

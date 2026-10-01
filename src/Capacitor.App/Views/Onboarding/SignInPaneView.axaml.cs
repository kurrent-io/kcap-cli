using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views.Onboarding;

public partial class SignInPaneView : UserControl {
    public SignInPaneView() => InitializeComponent();

    async void OnCopySignInUrlClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not SignInStepViewModel { BrowserUrl: { Length: > 0 } url }) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(url);
    }
}

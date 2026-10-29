using Avalonia.Controls;
using Avalonia.Interactivity;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views.Onboarding;

public partial class HistoryPaneView : UserControl {
    public HistoryPaneView() => InitializeComponent();

    void OnDocsClick(object? sender, RoutedEventArgs e) {
        try {
            new ShellUrlOpener().Open(HistoryStepViewModel.DocsUrl);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not open the docs: {ex.Message}");
        }
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views.Onboarding;

public partial class HarnessesPaneView : UserControl {
    public HarnessesPaneView() => InitializeComponent();

    // The options are a static list, so each radio carries its value and the page's one answer
    // lives on the view model rather than in a per-option flag.
    void OnVisibilityLoaded(object? sender, RoutedEventArgs e) {
        if (sender is RadioButton { Tag: string value } radio && DataContext is HarnessesStepViewModel vm)
            radio.IsChecked = vm.Visibility == value;
    }

    void OnVisibilityChecked(object? sender, RoutedEventArgs e) {
        if (sender is RadioButton { IsChecked: true, Tag: string value } && DataContext is HarnessesStepViewModel vm)
            vm.Visibility = value;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views.Onboarding;

public partial class HarnessesPaneView : UserControl {
    public HarnessesPaneView() => InitializeComponent();

    void OnVisibilityLoaded(object? sender, RoutedEventArgs e) {
        if (sender is not RadioButton { Tag: string value } radio || DataContext is not HarnessesStepViewModel vm) return;
        var subscription = vm.WhenAnyValue(x => x.Visibility).Subscribe(selected => radio.IsChecked = selected == value);
        EventHandler<VisualTreeAttachmentEventArgs>? detach = null;
        detach = (_, _) => {
            subscription.Dispose();
            radio.DetachedFromVisualTree -= detach;
        };
        radio.DetachedFromVisualTree += detach;
    }

    void OnVisibilityChecked(object? sender, RoutedEventArgs e) {
        if (sender is RadioButton { IsChecked: true, Tag: string value } && DataContext is HarnessesStepViewModel vm)
            vm.Visibility = value;
    }
}

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Capacitor.App.ViewModels.Onboarding;

namespace Capacitor.App.Views.Onboarding;

/// The onboarding flow as a pane of the main window. DataContext is the OnboardingViewModel.
public partial class OnboardingPaneView : UserControl {
    OnboardingViewModel? _observed;


    public OnboardingPaneView() {
        InitializeComponent();
        DataContextChanged += (_, _) => Observe(DataContext as OnboardingViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        Observe(DataContext as OnboardingViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        Observe(null);
        base.OnDetachedFromVisualTree(e);
    }

    void Observe(OnboardingViewModel? vm) {
        if (_observed is not null) _observed.PropertyChanged -= OnStepChanged;
        _observed = vm;
        if (_observed is not null) _observed.PropertyChanged += OnStepChanged;
    }

    void OnStepChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(OnboardingViewModel.Current)) return;
        StepScroll.Offset = default;
        CompactStepTitle.Opacity = 0;
        StepTitleText.Opacity = 1;
    }

    void OnStepScrolled(object? sender, ScrollChangedEventArgs e) {
        TitleBarRule.Opacity = StepScroll.Offset.Y > 0 ? 1 : 0;
        if (StepTitleText.Bounds.Height <= 0 || StepTitleText.TranslatePoint(default, StepScroll) is not { } position) return;
        var linear = Math.Clamp(-position.Y / StepTitleText.Bounds.Height, 0, 1);
        var progress = linear * linear * (3 - 2 * linear);
        StepTitleText.Opacity = 1 - progress;
        CompactStepTitle.Opacity = progress;
        CompactStepTitle.RenderTransform = new TranslateTransform(0, (1 - progress) * 8);
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}

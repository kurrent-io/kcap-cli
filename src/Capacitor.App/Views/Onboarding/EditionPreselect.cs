using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Views.Onboarding;

/// The edition card the page leads with: styled as selected through the <c>preselected</c> class and focused once
/// shown, so Enter takes it.
public static class EditionPreselect {
    public static readonly AttachedProperty<bool> IsPreselectedProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsPreselected", typeof(EditionPreselect));

    static EditionPreselect() {
        IsPreselectedProperty.Changed.AddClassHandler<Button>((card, e) => Apply(card, e.NewValue is true));
    }

    public static bool GetIsPreselected(Button card) => card.GetValue(IsPreselectedProperty);
    public static void SetIsPreselected(Button card, bool value) => card.SetValue(IsPreselectedProperty, value);

    static void Apply(Button card, bool preselected) {
        card.Classes.Set("preselected", preselected);
        if (!preselected) return;

        if (card.IsAttachedToVisualTree()) {
            FocusSoon(card);
            return;
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) {
            card.AttachedToVisualTree -= OnAttached;
            FocusSoon(card);
        }
        card.AttachedToVisualTree += OnAttached;
    }

    // Posted, so the step's template has finished laying out before focus moves.
    static void FocusSoon(Button card) =>
        Dispatcher.UIThread.Post(() => {
            if (GetIsPreselected(card) && card.IsAttachedToVisualTree() && card.IsEffectivelyEnabled) card.Focus();
        }, DispatcherPriority.Loaded);
}

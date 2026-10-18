using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Capacitor.App.Views.Onboarding;

namespace Capacitor.App.Tests.Unit;

/// The re-auth dialog's action rows follow the app's button order: dismiss first, primary last.
[NotInParallel("AvaloniaSession")]
public class SignInStepViewTests {
    static readonly AvaloniaProperty[] Chrome = [
        TemplatedControl.BackgroundProperty, TemplatedControl.ForegroundProperty, TemplatedControl.BorderBrushProperty,
        TemplatedControl.CornerRadiusProperty,
    ];

    [Test]
    public async Task Each_action_row_ends_with_its_primary() {
        var rows = await AvaloniaSession.DispatchAsync(() => new SignInStepView().GetLogicalDescendants().OfType<StackPanel>()
            .Where(panel => panel.Orientation == Orientation.Horizontal)
            .Select(panel => panel.Children.OfType<Button>().ToList())
            .Where(buttons => buttons.Any(IsPrimary))
            .Select(buttons => (Primary: buttons.Single(IsPrimary).Name, Last: buttons[^1].Name))
            .ToList());

        await Assert.That(rows).IsNotEmpty();
        await Assert.That(rows.Where(row => row.Primary != row.Last).Select(row => row.Primary)).IsEmpty();
    }

    /// The Kcap classes own a button's brushes and corners, so a restyle of the class reaches it.
    [Test]
    public async Task No_classed_button_restates_its_chrome() {
        var restated = await AvaloniaSession.DispatchAsync(() => new SignInStepView().GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("kcapPrimary") || button.Classes.Contains("kcapChip"))
            .SelectMany(button => Chrome.Where(button.IsSet).Select(property => $"{button.Name}.{property.Name}"))
            .ToList());

        await Assert.That(restated).IsEmpty();
    }

    static bool IsPrimary(Button button) => button.Classes.Contains("kcapPrimary");
}

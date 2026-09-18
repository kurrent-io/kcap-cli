using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class GlassChipTests {
    static (Window Window, Panel Scope) Show(Button chip, SurfaceMaterial material) {
        var scope = new Panel { Children = { chip } };
        MaterialScope.SetMaterial(scope, material);
        var window = new Window { Content = scope, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scope);
    }

    static Button Picker() => new() { Classes = { "kcapChip", "picker" }, Content = "repo" };

    [Test]
    public Task An_opaque_picker_keeps_the_pill_and_its_fill() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.Opaque);
        try {
            var presenter = chip.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ContentPresenter");
            await Assert.That(chip.Padding).IsEqualTo(new Thickness(11, 5));
            await Assert.That(chip.CornerRadius).IsEqualTo(new CornerRadius(999));
            await Assert.That(presenter.Background).IsNotNull();
            await Assert.That(chip.GetVisualDescendants().OfType<GlassLayer>().Any()).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_glass_picker_has_a_chip_layer_and_a_presenter_no_theme_style_can_fill() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.SoftGlass);
        try {
            var layer = chip.GetVisualDescendants().OfType<GlassLayer>().Single();
            // The vendored glass surface has a presenter of its own, named PART_ContentPresenter, deep
            // inside the layer: look the chip's up by name, never by type alone.
            var presenters = chip.GetVisualDescendants().OfType<ContentPresenter>().ToList();
            var presenter = presenters.Single(p => p.Name == "ChipContent");
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Chip);
            await Assert.That(layer.CornerRadius).IsEqualTo(new CornerRadius(12));
            // What Fluent's per-state styles target is a PART_ContentPresenter in the BUTTON's own template.
            await Assert.That(presenters.Any(p => p.Name == "PART_ContentPresenter" && ReferenceEquals(p.TemplatedParent, chip))).IsFalse();
            await Assert.That(chip.Padding).IsEqualTo(new Thickness(12, 7));

            foreach (var state in new[] { ":pointerover", ":pressed", ":disabled" }) {
                ((IPseudoClasses)chip.Classes).Set(state, true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(presenter.Background).IsNull();
                await Assert.That(presenter.BorderBrush).IsNull();
                ((IPseudoClasses)chip.Classes).Set(state, false);
            }
        } finally { window.Close(); }
    });

    [Test]
    public Task A_chip_that_is_not_a_picker_stays_opaque_under_glass() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = new Button { Classes = { "kcapChip" }, Content = "Activity" };
        var (window, _) = Show(chip, SurfaceMaterial.LiquidGlass);
        try {
            await Assert.That(chip.GetVisualDescendants().OfType<GlassLayer>().Any()).IsFalse();
        } finally { window.Close(); }
    });
}

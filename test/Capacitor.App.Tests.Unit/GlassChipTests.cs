using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

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
            var root = chip.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ChipRoot");
            // The vendored glass surface has a presenter of its own, named PART_ContentPresenter, deep
            // inside the layer: look the chip's up by name, never by type alone.
            var presenters = chip.GetVisualDescendants().OfType<ContentPresenter>().ToList();
            var presenter = presenters.Single(p => p.Name == "ChipContent");
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Chip);
            await Assert.That(layer.CornerRadius).IsEqualTo(new CornerRadius(12));
            // Without this, dropping the attribute blurs the chip's own label under itself.
            await Assert.That(LiquidGlassBackdrop.GetIsExcludedFromCapture(root)).IsTrue();
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

    /// The any-glass style is two selector arms around one template: without this, a dropped
    /// LiquidGlass arm would leave Liquid glass opaque and no test would notice.
    [Test]
    public Task A_liquid_glass_picker_takes_the_glass_template_too() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.LiquidGlass);
        try {
            await Assert.That(chip.GetVisualDescendants().OfType<GlassLayer>().Single().Kind).IsEqualTo(GlassKind.Chip);
            await Assert.That(chip.Padding).IsEqualTo(new Thickness(12, 7));
            await Assert.That(chip.GetVisualDescendants().OfType<ContentPresenter>().Any(p => p.Name == "ChipContent")).IsTrue();
            await Assert.That(Glass(chip).ChromaticAberration).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    public Task Each_state_moves_the_glass_it_is_meant_to_move() => AvaloniaSession.RunOnUiAsync(async () => {
        var chip = Picker();
        var (window, _) = Show(chip, SurfaceMaterial.SoftGlass);
        try {
            var glass = Glass(chip);
            var ring = chip.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "FocusRing");
            var root = chip.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "ChipRoot");
            var resting = (glass.TintColor, glass.SurfaceColor, glass.HighlightOpacity, glass.ShadowEnabled);
            await Assert.That(resting.HighlightOpacity).IsEqualTo(0.45);
            await Assert.That(resting.ShadowEnabled).IsTrue();
            await Assert.That(ring.IsVisible).IsFalse();
            await Assert.That(root.Opacity).IsEqualTo(1d);

            Set(chip, ":pointerover", true);
            await Assert.That(glass.TintColor).IsEqualTo(Color.Parse("#30DCEFFF"));
            await Assert.That(glass.HighlightOpacity).IsEqualTo(0.85);
            Set(chip, ":pointerover", false);

            Set(chip, ":pressed", true);
            await Assert.That(glass.SurfaceColor).IsEqualTo(Color.Parse("#80172533"));
            await Assert.That(glass.HighlightOpacity).IsEqualTo(0.4);
            await Assert.That(glass.ShadowEnabled).IsFalse();
            Set(chip, ":pressed", false);

            Set(chip, ":focus-visible", true);
            await Assert.That(ring.IsVisible).IsTrue();
            Set(chip, ":focus-visible", false);

            Set(chip, ":disabled", true);
            await Assert.That(root.Opacity).IsEqualTo(0.45);
            Set(chip, ":disabled", false);

            await Assert.That((glass.TintColor, glass.SurfaceColor, glass.HighlightOpacity, glass.ShadowEnabled)).IsEqualTo(resting);
        } finally { window.Close(); }
    });

    static LiquidGlassSurface Glass(Button chip) => chip.GetVisualDescendants().OfType<LiquidGlassSurface>().Single();

    static void Set(Button chip, string state, bool on) {
        ((IPseudoClasses)chip.Classes).Set(state, on);
        Dispatcher.UIThread.RunJobs();
    }
}

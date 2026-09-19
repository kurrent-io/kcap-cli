using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class GlassLayerTests {
    static (Window Window, Panel Scope, GlassLayer Layer) Build(GlassKind kind, SurfaceMaterial material) {
        var layer = new GlassLayer { Kind = kind, CornerRadius = new CornerRadius(18), Width = 200, Height = 100 };
        var scope = new Panel { Children = { layer } };
        MaterialScope.SetMaterial(scope, material);
        var window = new Window { Content = scope, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scope, layer);
    }

    static LiquidGlassSurface Glass(GlassLayer layer) => layer.GetVisualDescendants().OfType<LiquidGlassSurface>().Single();

    [Test]
    public Task The_scope_is_inherited_and_defaults_to_opaque() => AvaloniaSession.RunOnUiAsync(async () => {
        await Assert.That(MaterialScope.GetMaterial(new Button())).IsEqualTo(SurfaceMaterial.Opaque);
        var (window, _, layer) = Build(GlassKind.Card, SurfaceMaterial.LiquidGlass);
        try {
            await Assert.That(MaterialScope.GetMaterial(layer)).IsEqualTo(SurfaceMaterial.LiquidGlass);
        } finally { window.Close(); }
    });

    [Test]
    public Task A_card_takes_the_soft_then_the_liquid_parameters() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, scope, layer) = Build(GlassKind.Card, SurfaceMaterial.SoftGlass);
        try {
            var glass = Glass(layer);
            await Assert.That(glass.BlurRadius).IsEqualTo(14d);
            await Assert.That(glass.RefractionAmount).IsEqualTo(5d);
            await Assert.That(glass.ChromaticAberration).IsFalse();

            MaterialScope.SetMaterial(scope, SurfaceMaterial.LiquidGlass);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(glass.BlurRadius).IsEqualTo(5d);
            await Assert.That(glass.RefractionAmount).IsEqualTo(32d);
            await Assert.That(glass.ChromaticAberration).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_rail_takes_its_own_parameters() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, _, layer) = Build(GlassKind.Rail, SurfaceMaterial.SoftGlass);
        try {
            var glass = Glass(layer);
            await Assert.That(glass.BlurRadius).IsEqualTo(24d);
            await Assert.That(glass.HighlightFalloff).IsEqualTo(0.65);
        } finally { window.Close(); }
    });

    [Test]
    public Task The_host_radius_reaches_shader_and_rim_alike() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, _, layer) = Build(GlassKind.Card, SurfaceMaterial.SoftGlass);
        try {
            var rim = layer.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Rim");
            await Assert.That(Glass(layer).CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(rim.CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(layer.IsHitTestVisible).IsFalse();
        } finally { window.Close(); }
    });
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SurfaceTests {
    static (Window Window, Panel Scope) Show(Surface surface, SurfaceMaterial material) {
        var scope = new Panel { Children = { surface } };
        MaterialScope.SetMaterial(scope, material);
        var window = new Window { Content = scope, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, scope);
    }

    static IBrush Resource(string key) => (IBrush)Application.Current!.FindResource(key)!;

    [Test]
    public Task Opaque_is_one_border_with_no_glass_in_it() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { CornerRadius = new CornerRadius(12), Content = new TextBlock { Text = "x" } };
        var (window, _) = Show(surface, SurfaceMaterial.Opaque);
        try {
            await Assert.That(surface.GetVisualDescendants().OfType<LiquidGlassSurface>().Any()).IsFalse();
            var border = surface.GetVisualDescendants().OfType<Border>().First();
            await Assert.That(border.Background).IsEqualTo(Resource("KcapSurfaceBrush"));
            await Assert.That(border.BorderBrush).IsEqualTo(Resource("KcapBorderBrush"));
            await Assert.That(border.CornerRadius).IsEqualTo(new CornerRadius(12));
        } finally { window.Close(); }
    });

    [Test]
    public Task Raised_swaps_the_fill() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { Classes = { "raised" } };
        var (window, _) = Show(surface, SurfaceMaterial.Opaque);
        try {
            await Assert.That(surface.Background).IsEqualTo(Resource("KcapSurfaceRaisedBrush"));
        } finally { window.Close(); }
    });

    [Test]
    public Task Glass_keeps_the_site_radius_for_opaque_only_and_excludes_itself_from_capture() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { CornerRadius = new CornerRadius(12), Content = new TextBlock { Text = "x" } };
        var (window, _) = Show(surface, SurfaceMaterial.SoftGlass);
        try {
            var root = surface.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_GlassRoot");
            var layer = surface.GetVisualDescendants().OfType<GlassLayer>().Single();
            var content = root.Children.OfType<Border>().Single();
            await Assert.That(LiquidGlassBackdrop.GetIsExcludedFromCapture(root)).IsTrue();
            await Assert.That(layer.CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(content.CornerRadius).IsEqualTo(new CornerRadius(18));
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Card);
        } finally { window.Close(); }
    });

    [Test]
    public Task The_rail_class_selects_the_rail_kind_radius_and_rim() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { Classes = { "rail" } };
        var (window, _) = Show(surface, SurfaceMaterial.LiquidGlass);
        try {
            var layer = surface.GetVisualDescendants().OfType<GlassLayer>().Single();
            await Assert.That(layer.Kind).IsEqualTo(GlassKind.Rail);
            await Assert.That(layer.CornerRadius).IsEqualTo(new CornerRadius(22));
            await Assert.That(layer.BorderBrush).IsEqualTo(Resource("KcapGlassRimBrush"));
        } finally { window.Close(); }
    });

    [Test]
    public Task A_pinned_subtree_stays_opaque_inside_a_glass_scope() => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface();
        var pinned = new Panel { Children = { surface } };
        MaterialScope.SetMaterial(pinned, SurfaceMaterial.Opaque);
        var scope = new Panel { Children = { pinned } };
        MaterialScope.SetMaterial(scope, SurfaceMaterial.SoftGlass);
        var window = new Window { Content = scope };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(surface.GetVisualDescendants().OfType<GlassLayer>().Any()).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_material_switch_keeps_the_same_content_and_its_text() => AvaloniaSession.RunOnUiAsync(async () => {
        var box = new TextBox { Text = "ship it" };
        var surface = new Surface { Content = box };
        var (window, scope) = Show(surface, SurfaceMaterial.Opaque);
        try {
            MaterialScope.SetMaterial(scope, SurfaceMaterial.SoftGlass);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(ReferenceEquals(surface.Content, box)).IsTrue();
            await Assert.That(box.Text).IsEqualTo("ship it");
            await Assert.That(box.IsAttachedToVisualTree()).IsTrue();
        } finally { window.Close(); }
    });

    [Test]
    [Arguments(SurfaceMaterial.Opaque)]
    [Arguments(SurfaceMaterial.SoftGlass)]
    public Task Drag_over_rings_the_card_in_the_primary_brush(SurfaceMaterial material) => AvaloniaSession.RunOnUiAsync(async () => {
        var surface = new Surface { Classes = { "attachTarget" } };
        var (window, _) = Show(surface, material);
        try {
            var resting = surface.BorderBrush;
            surface.Classes.Add("dragOver");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(surface.BorderBrush).IsEqualTo(Resource("KcapPrimaryBrush"));
            await Assert.That(surface.BorderBrush).IsNotEqualTo(resting);
        } finally { window.Close(); }
    });
}

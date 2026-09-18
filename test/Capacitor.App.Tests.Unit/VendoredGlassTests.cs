using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Tests.Unit;

public class VendoredGlassTests {
    /// Pins the non-public member the vendored backdrop provider reflects on. If an Avalonia bump
    /// renames it, the lookup returns null and the backdrop silently stops refreshing.
    [Test]
    public async Task TopLevel_still_has_the_renderer_property_the_backdrop_provider_reflects_on() {
        var property = typeof(TopLevel).GetProperty(
            "Renderer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        await Assert.That(property).IsNotNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_glass_surface_renders_under_the_headless_backend() => AvaloniaSession.RunOnUiAsync(async () => {
        var window = new Window { Width = 400, Height = 300, Content = new LiquidGlassSurface { Width = 200, Height = 120 } };
        try {
            window.Show();
            for (var i = 0; i < 3; i++) {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            await Assert.That(window.IsVisible).IsTrue();
        } finally {
            window.Close();
        }
    });

    [Test]
    public async Task The_pipeline_reports_unavailability_once() {
        var reasons = new List<string>();
        void Handler(string reason) => reasons.Add(reason);
        LiquidGlassPipeline.Unavailable += Handler;
        try {
            LiquidGlassPipeline.Report("first");
            LiquidGlassPipeline.Report("second");
        } finally {
            LiquidGlassPipeline.Unavailable -= Handler;
        }
        await Assert.That(reasons.Count).IsLessThanOrEqualTo(1);
    }
}

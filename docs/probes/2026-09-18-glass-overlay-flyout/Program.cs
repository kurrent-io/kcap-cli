using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using LiquidGlassAvaloniaUI;

sealed class ProbeApp : Application {
    static readonly Uri App = new("avares://Kurrent Capacitor/");

    public override void Initialize() {
        RequestedThemeVariant = ThemeVariant.Dark;
        // Surface.axaml and SurfaceStyles.axaml resolve these from App.axaml, which a probe app is not.
        foreach (var (key, color) in new[] {
                     ("KcapSurfaceBrush", "#12151D"), ("KcapSurfaceRaisedBrush", "#191D27"),
                     ("KcapBorderBrush", "#2A3040"), ("KcapPrimaryBrush", "#F1F3F7"),
                 })
            Resources.Add(key, new SolidColorBrush(Color.Parse(color)));
        Resources.MergedDictionaries.Add(new ResourceInclude(App) { Source = new Uri("avares://Kurrent Capacitor/Controls/GlassLayer.axaml") });
        Resources.MergedDictionaries.Add(new ResourceInclude(App) { Source = new Uri("avares://Kurrent Capacitor/Controls/Surface.axaml") });
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(App) { Source = new Uri("avares://Kurrent Capacitor/Controls/GlassStyles.axaml") });
        Styles.Add(new StyleInclude(App) { Source = new Uri("avares://Kurrent Capacitor/Controls/SurfaceStyles.axaml") });
        // The flyout styles are the probe's own: the app ships no flyout glass.
        Styles.Add(new StyleInclude(new Uri("avares://Probe/")) { Source = new Uri("avares://Probe/ProbeGlassStyles.axaml") });
    }
}

// Hard edges every 16 px: blur is measurable as lost edge contrast.
sealed class Stripes : Control {
    static readonly IBrush A = new SolidColorBrush(Color.Parse("#20C0A0"));
    static readonly IBrush B = new SolidColorBrush(Color.Parse("#101020"));

    public override void Render(DrawingContext context) {
        for (var x = 0.0; x < Bounds.Width; x += 16)
            context.FillRectangle((int)(x / 16) % 2 == 0 ? A : B, new Rect(x, 0, 16, Bounds.Height));
    }
}

static class Program {
    const int W = 480, H = 320;
    static int _stride;
    static string? _unavailable;

    static int Main() {
        AppBuilder.Configure<ProbeApp>().UseSkia()
            // UseHeadlessDrawing selects the dummy backend; Skia only draws with it off.
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        LiquidGlassPipeline.Unavailable += reason => _unavailable = reason;

        // Controls, so a failure reads as "the flyout path" rather than "the harness": the same
        // glass layer over the same stripes, in a bare overlay-layer popup.
        Control("overlay popup", lightDismiss: false);
        Control("overlay popup + light dismiss", lightDismiss: true);

        var ok = Run("flyout, presenter template", "kcapPanelTemplated", wrapContent: false)
               & Run("flyout, wrapped content", "kcapPanel", wrapContent: true);

        var diagnostics = LiquidGlassDiagnostics.Snapshot;
        Console.WriteLine($"pipeline unavailable: {_unavailable ?? "no"}; captures published: {diagnostics.CapturesPublished}");
        ok &= _unavailable is null && diagnostics.CapturesPublished > 0;
        Console.WriteLine(ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    static Control Body(string text) => new TextBlock { Text = text, Foreground = Brushes.White, Width = 300, Height = 120 };

    static bool Run(string name, string presenterClass, bool wrapContent) {
        var glass = Capture("alpha", presenterClass, wrapContent, SurfaceMaterial.SoftGlass, out var material, out var region);
        var opaque = Capture("alpha", presenterClass, wrapContent, SurfaceMaterial.Opaque, out _, out _);
        var other = Capture("omega!!", presenterClass, wrapContent, SurfaceMaterial.SoftGlass, out _, out _);

        // Both edge samples take the SAME row, the panel covering its left and bare stripes its
        // right: a row picked in window coordinates instead can fall under the panel, and then the
        // control measures blurred stripes too.
        var row = region.Y + region.Height / 2;
        var outside = Gradient(glass, row, region.Right + 4, W - 4);
        var inside = Gradient(glass, row, region.X + 20, region.Right - 20);
        var differs = Diff(glass, opaque, region);
        // The lower third holds no text in either frame: a blurred ghost of the text would show here.
        var ghost = Diff(glass, other, new PixelRect(region.X + 20, region.Y + region.Height * 2 / 3, region.Width - 40, region.Height / 3 - 10));

        Console.WriteLine($"{name}: material={material} region={region} edge outside={outside:F1} inside={inside:F1} vs-opaque={differs}px text-ghost={ghost}px");
        return material == SurfaceMaterial.SoftGlass && differs > region.Width * region.Height / 2 && inside < outside / 4 && ghost == 0;
    }

    static byte[] Capture(
        string text, string presenterClass, bool wrapContent, SurfaceMaterial material,
        out SurfaceMaterial inherited, out PixelRect region) {
        var content = wrapContent ? GlassFlyouts.Panel(Body(text)) : Body(text);
        var flyout = new Flyout { Content = content };
        flyout.FlyoutPresenterClasses.Add(presenterClass);
        var owner = new Button { Content = "open", Flyout = flyout, Margin = new Thickness(24) };
        var scope = new Panel { Children = { new Stripes(), owner } };
        MaterialScope.SetMaterial(scope, material);
        GlassFlyouts.FollowMaterial(flyout, owner);
        var window = new Window { Width = W, Height = H, Content = scope };
        window.Show();
        flyout.ShowAt(owner);
        Pump();

        var presenter = (Control)flyout.Popup.Child!;
        inherited = MaterialScope.GetMaterial(presenter);
        if (material.IsGlass() && !flyout.Popup.IsUsingOverlayLayer)
            throw new InvalidOperationException("the popup is not in the overlay layer");

        var origin = presenter.TranslatePoint(default, window)!.Value;
        region = new PixelRect((int)origin.X, (int)origin.Y, (int)presenter.Bounds.Width, (int)presenter.Bounds.Height);

        var bytes = Pixels(window);
        flyout.Hide();
        window.Close();
        Pump();
        return bytes;
    }

    static void Control(string name, bool lightDismiss) {
        var layer = new GlassLayer { Kind = GlassKind.Panel, CornerRadius = new CornerRadius(12), Width = 300, Height = 140 };
        var popup = new Popup {
            ShouldUseOverlayLayer = true, Child = layer, IsLightDismissEnabled = lightDismiss,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft,
            PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight,
            HorizontalOffset = 90, VerticalOffset = 90,
        };
        var scope = new Panel { Children = { new Stripes(), popup } };
        MaterialScope.SetMaterial(scope, SurfaceMaterial.SoftGlass);
        var window = new Window { Width = W, Height = H, Content = scope };
        window.Show();
        Pump();
        popup.IsOpen = true;
        Pump();

        var origin = layer.TranslatePoint(default, window)!.Value;
        var region = new PixelRect((int)origin.X, (int)origin.Y, (int)layer.Bounds.Width, (int)layer.Bounds.Height);
        var bytes = Pixels(window);
        var row = region.Y + region.Height / 2;
        Console.WriteLine($"control {name}: region={region} edge outside={Gradient(bytes, row, region.Right + 4, W - 4):F1} inside={Gradient(bytes, row, region.X + 20, region.Right - 20):F1}");
        window.Close();
        Pump();
    }

    static byte[] Pixels(Window window) {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        using var buffer = frame.Lock();
        _stride = buffer.RowBytes;
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    static void Pump() {
        for (var i = 0; i < 12; i++) {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    static int Diff(byte[] a, byte[] b, PixelRect r) {
        var count = 0;
        for (var y = r.Y; y < r.Bottom; y++)
            for (var x = r.X; x < r.Right; x++) {
                var i = y * _stride + x * 4;
                if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 6) count++;
            }
        return count;
    }

    static double Gradient(byte[] a, int y, int x0, int x1) {
        double sum = 0;
        for (var x = x0 + 1; x < x1; x++) sum += Math.Abs(a[y * _stride + x * 4 + 1] - a[y * _stride + (x - 1) * 4 + 1]);
        return sum / (x1 - x0 - 1);
    }
}

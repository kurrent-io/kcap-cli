using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class MaterialWindowTests {
    static MaterialState State(SurfaceMaterial material) => MaterialState.Opaque with { Effective = material };

    static (MainWindow Window, BehaviorSubject<MaterialState> Material) Build(SurfaceMaterial material) {
        var states = new BehaviorSubject<MaterialState>(State(material));
        var vm = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(),
            TimeProvider.System, material: states);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, states);
    }

    [Test]
    public Task Without_a_material_source_the_window_is_opaque() => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New(), TimeProvider.System);
        var window = new MainWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(vm.Material).IsEqualTo(SurfaceMaterial.Opaque);
            await Assert.That(window.FindControl<MaterialBackdrop>("MaterialBackdrop")!.IsVisible).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task The_scope_follows_the_service_and_the_workspace_host_stays_opaque() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, material) = Build(SurfaceMaterial.SoftGlass);
        try {
            var sessions = window.FindControl<Grid>("SessionsSurface")!;
            var host = window.FindControl<ContentControl>("WorkspaceHost")!;
            await Assert.That(MaterialScope.GetMaterial(sessions)).IsEqualTo(SurfaceMaterial.SoftGlass);
            await Assert.That(MaterialScope.GetMaterial(host)).IsEqualTo(SurfaceMaterial.Opaque);
            await Assert.That(host.Background).IsNotNull();
            await Assert.That(window.FindControl<MaterialBackdrop>("MaterialBackdrop")!.IsVisible).IsTrue();

            material.OnNext(State(SurfaceMaterial.Opaque));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(MaterialScope.GetMaterial(sessions)).IsEqualTo(SurfaceMaterial.Opaque);
            await Assert.That(window.FindControl<MaterialBackdrop>("MaterialBackdrop")!.IsVisible).IsFalse();
        } finally { window.Close(); }
    });

    [Test]
    public Task The_rail_docks_when_opaque_and_floats_under_glass() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, material) = Build(SurfaceMaterial.Opaque);
        try {
            var rail = window.FindControl<SessionRailView>("SessionRail")!;
            var chrome = rail.FindControl<Grid>("RailChrome")!;
            await Assert.That(rail.Width).IsEqualTo(310d);
            await Assert.That(rail.Margin).IsEqualTo(new Thickness(0));
            await Assert.That(chrome.Height).IsEqualTo(44d);

            material.OnNext(State(SurfaceMaterial.LiquidGlass));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(rail.Width).IsEqualTo(310d);
            await Assert.That(rail.Margin).IsEqualTo(new Thickness(12, 40, 12, 12));
            await Assert.That(chrome.Height).IsEqualTo(16d);
            await Assert.That(rail.FindControl<Surface>("RailSurface")!.GlassKind).IsEqualTo(GlassKind.Rail);

            // Pins that the glass rail styles actually win: the rail's own UserControl.Styles sit
            // closer to its buttons than application styles, and App.axaml styles the same presenter.
            var presenter = rail.FindControl<Button>("RailNewSessionButton")!.GetVisualDescendants()
                .OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            await Assert.That(presenter.Background).IsEqualTo((IBrush)Application.Current!.FindResource("KcapGlassRailButtonBrush")!);
        } finally { window.Close(); }
    });
}

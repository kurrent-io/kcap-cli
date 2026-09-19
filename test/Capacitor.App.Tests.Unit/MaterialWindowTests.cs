using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Controls;
using Capacitor.App.Materials;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

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
            await Assert.That(host.Background).IsEqualTo((IBrush)Application.Current!.FindResource("KcapCanvasBrush")!);
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
            var railSurface = rail.FindControl<Surface>("RailSurface")!;
            await Assert.That(railSurface.GlassKind).IsEqualTo(GlassKind.Rail);
            var backdrop = window.FindControl<MaterialBackdrop>("MaterialBackdrop")!;
            await Assert.That(railSurface.Bounds.Width + rail.Margin.Left + rail.Margin.Right)
                .IsEqualTo(backdrop.RailWidth);

            // The glass button fill reaches the presenter even though the Button sets its own
            // Background locally.
            var presenter = rail.FindControl<Button>("RailNewSessionButton")!.GetVisualDescendants()
                .OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            await Assert.That(presenter.Background).IsEqualTo((IBrush)Application.Current!.FindResource("KcapGlassRailButtonBrush")!);
        } finally { window.Close(); }
    });

    /// A shown window whose rail holds one selected session row, under the given material.
    static (MainWindow Window, Button Row) RailRowWindow(SurfaceMaterial material) {
        var service = new FakeDaemonClientService();
        service.SnapshotsSubject.OnNext(Snap());
        service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        service.Agents.AddOrUpdate(new AgentStatusDto(
            "a1", "agent", "claude", "/dev/alpha/wt/feature-x", "Running",
            null, null, null, DateTime.UtcNow, null, null, Title: "Fix the flaky test"));

        Func<string, string> resolveRepoRoot = p => p.Contains("/wt/", StringComparison.Ordinal)
            ? p[..p.IndexOf("/wt/", StringComparison.Ordinal)]
            : p;
        var directory = new AgentDirectory(
            service, new FakeRemoteAgents(), new FakeServerLane(), new RepoIdentityResolver(_ => null),
            resolveRepoRoot, null, null, TimeProvider.System);
        var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, resolveRepoRoot);
        var vm = new MainWindowViewModel(service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
            rail: rail, material: new BehaviorSubject<MaterialState>(State(material)));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // The selection the rail paints, without opening a workspace: SwapTo sets exactly this.
        rail.SelectedAgentId = "a1";
        Dispatcher.UIThread.RunJobs();

        var row = window.GetVisualDescendants().OfType<Button>()
            .First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Fix the flaky test"));
        return (window, row);
    }

    /// The glass row fill and radius have to be declared where they outrank the rail's own opaque
    /// row styles, which set the same properties on the same element.
    [Test]
    public Task A_selected_rail_row_takes_the_glass_fill_under_glass() => AvaloniaSession.RunOnUiAsync(async () => {
        var (window, row) = RailRowWindow(SurfaceMaterial.SoftGlass);
        try {
            await Assert.That(row.Classes.Contains("selected")).IsTrue();
            await Assert.That(row.Background).IsEqualTo((IBrush)Application.Current!.FindResource("KcapGlassRailRowBrush")!);
            await Assert.That(row.CornerRadius).IsEqualTo(new CornerRadius(9));
        } finally { window.Close(); }
    });
}

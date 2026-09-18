using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Materials;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class SettingsMaterialTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly MaterialEnvironment Mac = new(GlassCapable: true, ReduceTransparency: false);

    SettingsViewModel Build(IMaterialService material) {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() {
            ["work"] = new Profile { ServerUrl = "https://work.example", Daemon = new DaemonSettings { Name = "daemon-a", MaxAgents = 5 } }
        } });
        return new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            new FakeDaemonClientService(), new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask,
            (_, _) => Task.FromResult(true), material: material);
    }

    [Test]
    public Task Picking_a_material_persists_it_and_moves_the_selection() => AvaloniaSession.RunOnUiAsync(async () => {
        var store = new InMemoryAppStateStore();
        using var service = new MaterialService(store, Mac, requested: null);
        using var vm = Build(service);
        await Assert.That(vm.IsSoftGlass).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsTrue();
        await Assert.That(vm.MaterialHint).IsNull();

        vm.IsLiquidGlass = true;
        Dispatcher.UIThread.RunJobs();

        await Assert.That(store.State.Material).IsEqualTo("liquid_glass");
        await Assert.That(vm.IsLiquidGlass).IsTrue();
        await Assert.That(vm.IsSoftGlass).IsFalse();
    });

    [Test]
    public Task A_machine_that_cannot_do_glass_disables_the_choices_and_says_why() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), new MaterialEnvironment(false, false), requested: null);
        using var vm = Build(service);
        await Assert.That(vm.IsOpaque).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsFalse();
        await Assert.That(vm.MaterialHint!).Contains("macOS");
    });

    [Test]
    public Task A_view_model_created_after_a_failure_shows_the_reason() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.SoftGlass);
        service.ReportPipelineFailure("shader did not compile");
        using var vm = Build(service);
        await Assert.That(vm.IsOpaque).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsFalse();
        await Assert.That(vm.MaterialHint!).Contains("shader did not compile");
    });

    [Test]
    public Task Reduce_transparency_explains_the_opaque_default_and_leaves_the_choices_on() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), Mac with { ReduceTransparency = true }, requested: null);
        using var vm = Build(service);
        await Assert.That(vm.IsOpaque).IsTrue();
        await Assert.That(vm.MaterialChoicesEnabled).IsTrue();
        await Assert.That(vm.MaterialHint!).Contains("Reduce transparency");
    });

    [Test]
    public Task The_window_binds_the_three_choices_and_the_hint() => AvaloniaSession.RunOnUiAsync(async () => {
        using var service = new MaterialService(new InMemoryAppStateStore(), Mac, SurfaceMaterial.Opaque);
        using var vm = Build(service);
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(window.FindControl<RadioButton>("MaterialOpaque")!.IsChecked).IsTrue();
            await Assert.That(window.FindControl<RadioButton>("MaterialSoftGlass")!.Classes.Contains("kcapChoice")).IsTrue();
            await Assert.That(window.FindControl<RadioButton>("MaterialLiquidGlass")!.IsEffectivelyEnabled).IsTrue();
            await Assert.That(window.FindControl<TextBlock>("MaterialHintText")!.IsVisible).IsFalse();
        } finally { window.Close(); }
    });
}

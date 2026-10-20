using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class UiScaleWindowTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly RawInputModifiers Modifier =
        RefreshShortcut.UsesControl ? RawInputModifiers.Control : RawInputModifiers.Meta;

    Task<UiScaleSetting> NewSetting() => UiScaleSetting.LoadAsync(new AppStateStore(Config.Root.Path("app-state.json")));

    static double? Applied(MainWindow window) =>
        (window.FindControl<LayoutTransformControl>("ScaleHost")!.LayoutTransform as ScaleTransform)?.ScaleX;

    [Test]
    public Task Main_window_shortcuts_step_the_scale_and_magnify_its_content() => AvaloniaSession.RunOnUiAsync(async () => {
        var setting = await NewSetting();
        var window = new MainWindow { ContentScale = setting };
        try {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(Applied(window)).IsNull();

            window.KeyPressQwerty(PhysicalKey.Equal, Modifier);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(setting.Current).IsEqualTo(1.1);
            await Assert.That(Applied(window)).IsEqualTo(1.1);

            window.KeyPressQwerty(PhysicalKey.Minus, Modifier);
            window.KeyPressQwerty(PhysicalKey.Minus, Modifier);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(setting.Current).IsEqualTo(0.9);
            await Assert.That(Applied(window)).IsEqualTo(0.9);
        } finally { window.Close(); }
    });

    [Test]
    public Task Appearance_tab_steps_resets_and_follows_a_change_made_elsewhere() => AvaloniaSession.RunOnUiAsync(async () => {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() { ["work"] = new Profile { ServerUrl = "https://work.example" } } });
        var setting = await NewSetting();
        using var vm = new SettingsViewModel(new SettingsProfileStore(Config.Root, "work", "https://work.example"),
            new FakeDaemonClientService(), new ScriptedLocalControlOps(), (_, _) => Task.FromResult(false),
            (_, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
            (_, _) => Task.FromResult(false), _ => Task.FromResult(false), true, Task.CompletedTask, (_, _) => Task.FromResult(true)) {
            Appearance = new AppearanceSettingsViewModel(setting),
        };
        var window = new SettingsWindow { DataContext = vm };
        try {
            window.Show();
            window.FindControl<TabControl>("SettingsTabs")!.SelectedItem = window.FindControl<TabItem>("AppearanceTab");
            Dispatcher.UIThread.RunJobs();
            var label   = window.FindControl<TextBlock>("TextSizeText")!;
            var larger  = window.FindControl<Button>("TextLargerButton")!;
            var reset   = window.FindControl<Button>("TextResetButton")!;
            await Assert.That(label.Text).IsEqualTo("100%");
            await Assert.That(reset.IsEffectivelyEnabled).IsFalse();

            await vm.Appearance.LargerCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(label.Text).IsEqualTo("110%");
            await Assert.That(reset.IsEffectivelyEnabled).IsTrue();

            await setting.SetAsync(UiScale.Max);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(label.Text).IsEqualTo("150%");
            await Assert.That(larger.IsEffectivelyEnabled).IsFalse();

            await vm.Appearance.ResetCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(setting.Current).IsEqualTo(UiScale.Default);
            await Assert.That(label.Text).IsEqualTo("100%");
        } finally { window.Close(); }
    });
}

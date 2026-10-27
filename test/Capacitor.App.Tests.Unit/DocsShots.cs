using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

/// Names the file a scene test writes, so the name pin can read it.
[AttributeUsage(AttributeTargets.Method)]
sealed class DocsShotAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// Renders the desktop app's docs figures when KCAP_SHOTS_OUT is the absolute path of an existing directory; skipped otherwise.
[NotInParallel("AvaloniaSession")]
public sealed class DocsShots {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const int MaxPumps = 200;

    static string OutDir {
        get {
            var dir = Environment.GetEnvironmentVariable(DocsShotScenes.OutEnvVar);
            Skip.Unless(dir is not null && Path.IsPathRooted(dir) && Directory.Exists(dir),
                $"Docs screenshot harness: set {DocsShotScenes.OutEnvVar} to the absolute path of an existing directory to render the figures");
            return dir!;
        }
    }

    static async Task CaptureAsync(string name, Func<Task<Window>> build, string sentinelControlName, Action<Window>? afterShow = null) {
        var path = Path.Combine(OutDir, $"{name}-dark.png");
        await AvaloniaSession.DispatchAsync(async () => {
            var window = await build();
            try {
                window.Show();
                window.SetRenderScaling(2);
                afterShow?.Invoke(window);
                for (var i = 0; i < MaxPumps && !Ready(window, sentinelControlName); i++) {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    window.UpdateLayout();
                }
                if (!Ready(window, sentinelControlName))
                    throw new TimeoutException($"{name}: '{sentinelControlName}' never became ready");
                var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException($"{name}: the window rendered no frame to capture");
                frame.Save(path, new PngBitmapEncoderOptions());
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
                (window.DataContext as IDisposable)?.Dispose();
            }
            return true;
        });
    }

    static bool Ready(Window window, string name) {
        var control = window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        return control switch {
            TextBlock text => !string.IsNullOrEmpty(text.Text),
            { } c => c.IsVisible,
            _ => false
        };
    }

    [Test]
    public async Task The_scene_names_match_the_docs_manifest() {
        await Assert.That(DocsShotScenes.Names).IsEquivalentTo([
            "desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings",
            "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-local-recovery"]);
    }

    [Test]
    public async Task Every_scene_test_names_a_known_scene_once() {
        var named = typeof(DocsShots).GetMethods()
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => m.GetCustomAttribute<DocsShotAttribute>()?.Name)
            .OfType<string>().ToArray();
        await Assert.That(named.Except(DocsShotScenes.Names)).IsEmpty();
        await Assert.That(named.Distinct().Count()).IsEqualTo(named.Length);
    }

    [Test, DocsShot("desktop-main")]
    public async Task Main_window_with_rail_and_a_working_chat_tab() {
        await CaptureAsync("desktop-main", async () => (await DocsShotScenes.MainWindowAsync()).Window, "ToolRowLabel");
    }

    [Test, DocsShot("desktop-permission-card")]
    public async Task A_permission_request_waits_in_the_tab() {
        await CaptureAsync("desktop-permission-card", async () => {
            var scene = await DocsShotScenes.MainWindowAsync(pendingIds: new HashSet<string> { "a1" });
            scene.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Bash",
                toolInputJson: """{"command":"git push origin feat/gateway-retry"}"""));
            return scene.Window;
        }, "PermissionCard");
    }

    [Test, DocsShot("desktop-help-menu")]
    public async Task The_help_and_support_flyout_is_open() {
        await CaptureAsync("desktop-help-menu",
            async () => (await DocsShotScenes.MainWindowAsync(openFeedback: _ => { }, requestSetup: () => { }, openSession: false)).Window,
            "RailHelpDocsButton", afterShow: OpenHelpFlyoutInsideTheWindow);
    }

    [Test, DocsShot("desktop-settings")]
    public async Task The_settings_window_shows_the_profile_and_daemon() {
        await CaptureAsync("desktop-settings", () => Task.FromResult<Window>(DocsShotScenes.Settings(Config)), "SettingsTabs");
    }

    [Test, DocsShot("desktop-wizard-edition")]
    public async Task The_setup_wizard_offers_the_cloud_and_local_editions() {
        await CaptureAsync("desktop-wizard-edition", async () => await DocsShotScenes.WizardAsync(Config, WizardStepId.Edition), "CloudEditionButton");
    }

    [Test, DocsShot("desktop-wizard-sign-in")]
    public async Task The_setup_wizard_asks_to_sign_in_with_a_work_account() {
        await CaptureAsync("desktop-wizard-sign-in", async () => await DocsShotScenes.WizardAsync(Config, WizardStepId.SignIn), "WorkAccountButton");
    }

    static void OpenHelpFlyoutInsideTheWindow(Window window) {
        var help = window.FindDescendantOfType<SessionRailView>()!.FindControl<Button>("RailHelpButton")!;
        ((Flyout)help.Flyout!).ShowAt(help);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var presenter = window.FindDescendantOfType<FlyoutPresenter>()
            ?? throw new InvalidOperationException("desktop-help-menu: the help flyout has no presenter in the window");
        var bounds = new Rect(presenter.Bounds.Size).TransformToAABB(presenter.TransformToVisual(window)!.Value);
        var frame = new Rect(window.Bounds.Size);
        if (!frame.Contains(bounds))
            throw new InvalidOperationException(
                $"desktop-help-menu: the help flyout at {bounds} overflows the window {frame} " +
                $"(left {Math.Max(0, -bounds.X)}, top {Math.Max(0, -bounds.Y)}, " +
                $"right {Math.Max(0, bounds.Right - frame.Width)}, bottom {Math.Max(0, bounds.Bottom - frame.Height)})");
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

/// Renders the desktop app's docs figures into KCAP_SHOTS_OUT, which must be the absolute path of an
/// existing directory; skipped when it is unset.
[NotInParallel("AvaloniaSession")]
public sealed class DocsShots {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const int MaxPumps = 200;

    static string OutDir {
        get {
            var dir = Environment.GetEnvironmentVariable(DocsShotScenes.OutEnvVar);
            Skip.When(dir is null,
                $"Docs screenshot harness: set {DocsShotScenes.OutEnvVar} to the absolute path of an existing directory to render the figures");
            if (!Path.IsPathFullyQualified(dir) || !Directory.Exists(dir))
                throw new InvalidOperationException(
                    $"{DocsShotScenes.OutEnvVar} is '{dir}', which is not the absolute path of an existing directory");
            return dir;
        }
    }

    /// Renders the scene to the file the calling test's DocsShot attribute names, with no text caret.
    static async Task CaptureAsync(Func<Task<DocsShotScene>> build, string sentinelControlName,
            Action<Window>? afterShow = null, [CallerMemberName] string test = "") {
        var name = typeof(DocsShots).GetMethod(test)?.GetCustomAttribute<DocsShotAttribute>()?.Name
            ?? throw new InvalidOperationException($"{test} captures a docs figure but carries no DocsShot attribute");
        var path = Path.Combine(OutDir, $"{name}-dark.png");
        await AvaloniaSession.DispatchAsync(async () => {
            await using var scene = await build();
            var window = scene.Window;
            window.Show();
            window.SetRenderScaling(2);
            afterShow?.Invoke(window);
            for (var i = 0; i < MaxPumps && !Ready(window, sentinelControlName); i++) Pump(window);
            if (Find(window, sentinelControlName) is null)
                throw new TimeoutException($"{name}: no control named '{sentinelControlName}' appeared");
            if (!Ready(window, sentinelControlName))
                throw new TimeoutException($"{name}: '{sentinelControlName}' is present but never became ready");
            // Deactivating the window keeps the caret; only dropping focus hides it. An open flyout
            // holds focus on a button and closes when focus leaves it, so only a text box loses it.
            if (window.FocusManager?.GetFocusedElement() is TextBox) window.FocusManager.Focus(null);
            Pump(window);
            var frame = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException($"{name}: the window rendered no frame to capture");
            frame.Save(path, new PngBitmapEncoderOptions());
            return true;
        });
    }

    static void Pump(Window window) {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.UpdateLayout();
    }

    static Control? Find(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    static bool Ready(Window window, string name) => Find(window, name) switch {
        TextBlock text => !string.IsNullOrEmpty(text.Text),
        TextBox box => !string.IsNullOrEmpty(box.Text),
        { } c => c.IsEffectivelyVisible,
        _ => false
    };

    [Test]
    public async Task The_scene_names_match_the_docs_manifest() {
        await Assert.That(DocsShotScenes.Names).IsEquivalentTo([
            "desktop-main", "desktop-permission-card", "desktop-help-menu", "desktop-settings",
            "desktop-wizard-edition", "desktop-wizard-sign-in", "desktop-local-recovery"]);
    }

    [Test]
    public async Task Every_scene_has_exactly_one_test() {
        var named = typeof(DocsShots).GetMethods()
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
            .Select(m => m.GetCustomAttribute<DocsShotAttribute>()?.Name)
            .OfType<string>().ToArray();
        await Assert.That(named).IsEquivalentTo(DocsShotScenes.Names);
    }

    [Test, DocsShot("desktop-main")]
    public async Task Main_window_with_rail_and_a_working_chat_tab() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(), "ToolRowLabel");
    }

    [Test, DocsShot("desktop-permission-card")]
    public async Task A_permission_request_waits_in_the_tab() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(pendingIds: new HashSet<string> { "a1" },
            permissionRequests: [PermissionEntries.Entry("r1", "a1", toolName: "Bash",
                toolInputJson: """{"command":"git push origin feat/gateway-retry"}""")]), "PermissionCard");
    }

    [Test, DocsShot("desktop-help-menu")]
    public async Task The_help_and_support_flyout_is_open() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(openFeedback: _ => { }, requestSetup: () => { }, openSession: false),
            "RailHelpDocsButton", afterShow: OpenHelpFlyoutInsideTheWindow);
    }

    [Test, DocsShot("desktop-settings")]
    public async Task The_settings_window_shows_the_profile_and_daemon() {
        await CaptureAsync(() => DocsShotScenes.SettingsAsync(Config), "NameInput");
    }

    [Test, DocsShot("desktop-wizard-edition")]
    public async Task The_setup_wizard_offers_the_cloud_and_local_editions() {
        await CaptureAsync(() => DocsShotScenes.WizardAsync(Config, WizardStepId.Edition), "CloudEditionButton");
    }

    [Test, DocsShot("desktop-wizard-sign-in")]
    public async Task The_setup_wizard_asks_to_sign_in_with_a_work_account() {
        await CaptureAsync(() => DocsShotScenes.WizardAsync(Config, WizardStepId.SignIn), "WorkAccountButton");
    }

    [Test, DocsShot("desktop-local-recovery")]
    public async Task The_launcher_offers_to_start_the_local_server() {
        await CaptureAsync(() => DocsShotScenes.MainWindowAsync(openSession: false, localServerDown: true), "StartLocalServerButton");
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

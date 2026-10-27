using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Capacitor.App.Tests.Unit;

/// Names the file a scene test writes, so the name pin can read it.
[AttributeUsage(AttributeTargets.Method)]
sealed class DocsShotAttribute(string name) : Attribute {
    public string Name { get; } = name;
}

/// Renders the desktop app's docs figures when KCAP_SHOTS_OUT names an existing directory; skipped otherwise.
[NotInParallel("AvaloniaSession")]
public sealed class DocsShots {
    const int MaxPumps = 200;

    static string OutDir {
        get {
            var dir = Environment.GetEnvironmentVariable(DocsShotScenes.OutEnvVar);
            Skip.Unless(dir is not null && Directory.Exists(dir),
                $"Docs screenshot harness: set {DocsShotScenes.OutEnvVar} to an existing directory to render the figures");
            return dir!;
        }
    }

    static async Task CaptureAsync(string name, Func<Task<Window>> build, string sentinelControlName) {
        var path = Path.Combine(OutDir, $"{name}-dark.png");
        await AvaloniaSession.DispatchAsync(async () => {
            var window = await build();
            try {
                window.Show();
                window.SetRenderScaling(2);
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
}

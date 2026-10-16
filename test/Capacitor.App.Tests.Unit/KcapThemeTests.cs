using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class KcapThemeTests {
    sealed class StatefulButton : Button {
        protected override Type StyleKeyOverride => typeof(Button);
        public void State(string state, bool enabled) => PseudoClasses.Set(state, enabled);
    }

    static Color ColorFor(string key) => ((ISolidColorBrush)Application.Current!.FindResource(key)!).Color;

    [Test]
    [Arguments("kcapPrimary", "KcapPrimaryBrush")]
    [Arguments("kcapDangerPrimary", "KcapDangerBrush")]
    public Task Filled_buttons_keep_the_palette_in_every_interaction_state(string cssClass, string fill) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var button = new StatefulButton { Content = "Confirm", Classes = { cssClass } };
            var window = new Window { Content = button };
            window.Show();
            try {
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
                    .Single(p => p.Name == "PART_ContentPresenter");
                await Assert.That(((ISolidColorBrush)presenter.Background!).Color).IsEqualTo(ColorFor(fill));
                await Assert.That(((ISolidColorBrush)presenter.Foreground!).Color).IsEqualTo(ColorFor("KcapOnPrimaryBrush"));
                button.State(":pointerover", true);
                await Assert.That(((ISolidColorBrush)presenter.Background!).Color).IsEqualTo(ColorFor("KcapMutedBrush"));
                button.State(":pressed", true);
                await Assert.That(((ISolidColorBrush)presenter.Background!).Color).IsEqualTo(ColorFor("KcapFaintBrush"));
                button.IsEnabled = false;
                await Assert.That(((ISolidColorBrush)presenter.Background!).Color).IsEqualTo(ColorFor("KcapBorderBrush"));
            } finally { window.Close(); }
        });

    [Test]
    public Task Vendor_selection_stays_visible_on_hover_and_press() => AvaloniaSession.RunOnUiAsync(async () => {
        var button = new StatefulButton { Classes = { "kcapGhost", "active" } };
        var window = new Window { Content = button };
        window.Show();
        try {
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(p => p.Name == "PART_ContentPresenter");
            button.State(":pointerover", true);
            button.State(":pressed", true);
            await Assert.That(((ISolidColorBrush)presenter.Background!).Color).IsEqualTo(ColorFor("KcapSelectionBrush"));
        } finally { window.Close(); }
    });

    [Test]
    [Arguments(LifecyclePrompt.KindRemoveProfile, true)]
    [Arguments(LifecyclePrompt.KindRename, false)]
    [Arguments(LifecyclePrompt.KindRepair, false)]
    public Task Lifecycle_confirmation_rank_matches_the_action(string kind, bool destructive) =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var vm = new LifecyclePromptViewModel(new LifecyclePrompt(kind, null, null, false, "Details"), new());
            var window = new LifecyclePromptWindow { DataContext = vm };
            window.Show();
            try {
                var accept = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
                await Assert.That(accept.Classes.Contains("kcapDangerPrimary")).IsEqualTo(destructive);
                await Assert.That(accept.Classes.Contains("kcapPrimary")).IsEqualTo(!destructive);
                await Assert.That(((ISolidColorBrush)window.Background!).Color).IsEqualTo(ColorFor("KcapCanvasBrush"));
            } finally { window.Close(); }
        });

    [Test]
    public Task Code_built_dialogs_use_the_canvas_and_button_ranks() => AvaloniaSession.RunOnUiAsync(async () => {
        var startup = App.BuildStartupErrorWindow(new InvalidOperationException("Details"));
        var stop = App.BuildConfirmForceStopWindow("Agent", new());
        startup.Show();
        stop.Show();
        try {
            await Assert.That(((ISolidColorBrush)startup.Background!).Color).IsEqualTo(ColorFor("KcapCanvasBrush"));
            await Assert.That(((ISolidColorBrush)startup.Foreground!).Color).IsEqualTo(ColorFor("KcapTextBrush"));
            await Assert.That(((ISolidColorBrush)stop.Background!).Color).IsEqualTo(ColorFor("KcapCanvasBrush"));
            var confirm = stop.GetVisualDescendants().OfType<Button>().Single(b => b.IsDefault);
            var cancel = stop.GetVisualDescendants().OfType<Button>().Single(b => b.IsCancel);
            await Assert.That(confirm.Classes.Contains("kcapDangerPrimary")).IsTrue();
            await Assert.That(cancel.Classes.Contains("kcapChip")).IsTrue();
        } finally { startup.Close(); stop.Close(); }
    });

    [Test]
    [Arguments("antigravity")]
    [Arguments("pi")]
    [Arguments("gemini")]
    [Arguments("kiro")]
    public Task Vendor_identity_uses_neutral_text(string vendor) => AvaloniaSession.RunOnUiAsync(async () => {
        await Assert.That(Color.Parse(HostedHarnessCatalog.TileFor(vendor).Color)).IsEqualTo(ColorFor("KcapTextBrush"));
    });
}

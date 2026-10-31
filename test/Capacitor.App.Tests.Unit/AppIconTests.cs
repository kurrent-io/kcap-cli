using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Lucide.Avalonia;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class AppIconTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Lucide_icons_load_and_keep_square_slots_at_desktop_sizes() {
        await RunOnUiAsync(async () => {
            var panel = new StackPanel();
            var kinds = Enum.GetValues<ToolCategory>().Select(ToolCategoryIcons.ForCategory)
                .Concat(new[] { LucideIconKind.CircleCheck, LucideIconKind.CircleX,
                    LucideIconKind.CircleQuestionMark, LucideIconKind.LoaderCircle,
                    LucideIconKind.GitPullRequestDraft, LucideIconKind.GitMerge,
                    LucideIconKind.ChevronDown, LucideIconKind.ChevronRight });
            foreach (var kind in kinds.Distinct())
                foreach (var size in new[] { 12d, 14d, 16d })
                    panel.Children.Add(new AppIcon { Kind = kind, Size = size, Foreground = Brushes.White });
            var spinner = panel.Children.OfType<AppIcon>().First(icon => icon.Kind == LucideIconKind.LoaderCircle);
            spinner.Classes.Add("kcapSpinning");

            var window = new Window { Content = panel, Width = 200, Height = 1600 };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                foreach (var icon in panel.Children.OfType<AppIcon>()) {
                    await Assert.That(icon.Bounds.Size).IsEqualTo(new Size(icon.Size, icon.Size));
                    await Assert.That(icon.StrokeWidth).IsEqualTo(2d);
                }
                await Assert.That(spinner.RenderTransform).IsNotNull();
                // A bound header can lose and regain its icon as live tool summaries change.
                var header = new ChatKindHeader { Label = "Command", IconKind = ToolCategoryIcons.CommandIcon };
                window.Content = header;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var headerIcon = header.GetVisualDescendants().OfType<AppIcon>().Single();
                await Assert.That(headerIcon.Kind).IsEqualTo(LucideIconKind.Terminal);
                await Assert.That(headerIcon.IsEffectivelyVisible).IsTrue();
                header.IconKind = null;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(headerIcon.IsEffectivelyVisible).IsFalse();
            } finally { window.Close(); }
        });
    }

    /// An unnamed icon stays out of what a screen reader walks; a named one is an image that
    /// speaks its name, with none of the glyph's drawing parts beneath it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Only_a_named_icon_is_exposed_to_assistive_technology() {
        await RunOnUiAsync(async () => {
            var plain = ControlAutomationPeer.CreatePeerForElement(new AppIcon { Kind = LucideIconKind.Check });
            var named = new AppIcon { Kind = LucideIconKind.Check };
            AutomationProperties.SetName(named, "Done");
            var peer = ControlAutomationPeer.CreatePeerForElement(named);

            await Assert.That(plain.IsControlElement()).IsFalse();
            await Assert.That(plain.IsContentElement()).IsFalse();
            await Assert.That(peer.IsControlElement()).IsTrue();
            await Assert.That(peer.GetAutomationControlType()).IsEqualTo(AutomationControlType.Image);
            await Assert.That(peer.GetName()).IsEqualTo("Done");
            await Assert.That(peer.GetChildren()).IsEmpty();
        });
    }
}

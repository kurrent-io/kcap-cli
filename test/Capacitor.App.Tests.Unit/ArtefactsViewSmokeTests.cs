using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The Artefacts view on its own: the PAGES section lists a session's pages as rows, or says why it
/// has none.
public class ArtefactsViewSmokeTests {
    const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    static async Task<(Window Window, ArtefactsTabViewModel Vm)> HostAsync(SessionPagesRead pages) {
        var source = new FakePageSource();
        source.Enqueue(pages);
        var vm = new ArtefactsTabViewModel(new FakePlanArtifactSource(), new PlanActivity(), TimeProvider.System, pages: source);
        var window = new Window { Content = new ArtefactsView { DataContext = vm }, Width = 900, Height = 600 };
        window.Show();
        vm.SwitchSession(Session, "/repo");
        await (vm.PendingPagesReadForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, vm);
    }

    static IEnumerable<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "");

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Pages_section_renders_rows_and_note() => RunOnUiAsync(async () => {
        var page = new ArtefactDto("01ec", "Release checklist", "u1", "org", 3, DateTimeOffset.UtcNow, true, "https://kurrent.kcap.ai/artefacts/01ec");
        var (window, vm) = await HostAsync(new SessionPagesRead(SessionPlansReadKind.Ready, [page]));
        var rows = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("artefactRow")).ToList();
        await Assert.That(Texts(window)).Contains("PAGES");
        await Assert.That(rows.Count).IsEqualTo(1);
        var row = rows[0];
        await Assert.That(row.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)).Contains("Release checklist");
        await Assert.That(row.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text!.StartsWith("Page · v3 · ", StringComparison.Ordinal))).IsTrue();
        await Assert.That(ToolTip.GetTip(row)).IsEqualTo(page.Url);
        await Assert.That(Texts(window).Any(t => t == "No pages yet.")).IsFalse();
        window.Close();
        await vm.TeardownAsync();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Pages_section_shows_the_note_when_the_server_has_no_pages() => RunOnUiAsync(async () => {
        var (window, vm) = await HostAsync(SessionPagesRead.Of(SessionPlansReadKind.Unavailable));
        await Assert.That(Texts(window)).Contains("Pages unavailable on this server.");
        window.Close();
        await vm.TeardownAsync();
    });

    /// The right pane swaps the reader for the page viewer. A page that is not found never reaches
    /// Ready, so no NativeWebView is built under the headless platform.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Selecting_a_page_shows_the_viewer_in_the_right_pane() => RunOnUiAsync(async () => {
        var page = new ArtefactDto("01ec", "Release checklist", "u1", "org", 3, DateTimeOffset.UtcNow, true, "https://kurrent.kcap.ai/pages/01ec");
        var source = new FakePageSource();
        source.Enqueue(new SessionPagesRead(SessionPlansReadKind.Ready, [page]));
        var vm = new ArtefactsTabViewModel(new FakePlanArtifactSource(), new PlanActivity(), TimeProvider.System, pages: source,
            opener: new RecordingOpener(), webView: new FixedWebViewAvailability(true));
        var window = new Window { Content = new ArtefactsView { DataContext = vm }, Width = 900, Height = 600 };
        window.Show();
        vm.SwitchSession(Session, "/repo");
        await (vm.PendingPagesReadForTesting ?? Task.CompletedTask);

        await vm.OpenPageCommand.Execute(vm.Pages[0]);
        await (vm.PendingPageLoadForTesting ?? Task.CompletedTask);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        await Assert.That(window.GetVisualDescendants().OfType<PageViewer>().Count()).IsEqualTo(1);
        await Assert.That(Texts(window)).Contains("This page is gone, or you can't see it.");
        await Assert.That(Texts(window).Any(t => t == "Select a document to read it.")).IsFalse();
        await Assert.That(window.GetVisualDescendants().OfType<NativeWebView>().Any()).IsFalse();
        var row = window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("artefactRow"));
        await Assert.That(row.Classes.Contains("selected")).IsTrue();
        window.Close();
        await vm.TeardownAsync();
    });
}

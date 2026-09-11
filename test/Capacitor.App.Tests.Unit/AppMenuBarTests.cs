using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class AppMenuBarTests {
    static AppMenuBar NewBar(RecordingOpener? opener = null, Action? showMainWindow = null, IReadOnlyList<Window>? windows = null) =>
        new(opener ?? new RecordingOpener(), () => windows ?? [], () => showMainWindow);

    static NativeMenu Submenu(NativeMenu bar, string header) => Item(bar, header).Menu!;

    static NativeMenuItem Item(NativeMenu menu, string header) =>
        menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == header);

    static string Layout(NativeMenu menu) =>
        string.Join("|", menu.Items.Select(i => i is NativeMenuItem item ? item.Header : "-"));

    static void Click(NativeMenuItem item) => ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Build_puts_Window_before_Help() {
        var layout = await AvaloniaSession.DispatchAsync(() => Layout(NewBar().Build(new Window())));

        await Assert.That(layout).IsEqualTo("Window|Help");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_menu_offers_the_main_window_when_one_can_be_shown() {
        var layout = await AvaloniaSession.DispatchAsync(() =>
            Layout(Submenu(NewBar(showMainWindow: () => { }).Build(new Window()), "Window")));

        await Assert.That(layout).IsEqualTo("Minimize|Zoom|Toggle Full Screen|-|Kurrent Capacitor|-|Bring All to Front");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_menu_leaves_out_the_main_window_before_one_exists() {
        var layout = await AvaloniaSession.DispatchAsync(() => Layout(Submenu(NewBar().Build(new Window()), "Window")));

        await Assert.That(layout).IsEqualTo("Minimize|Zoom|Toggle Full Screen|-|Bring All to Front");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Window_menu_carries_the_standard_macOS_shortcuts() {
        var (minimize, fullScreen, main) = await AvaloniaSession.DispatchAsync(() => {
            var menu = Submenu(NewBar(showMainWindow: () => { }).Build(new Window()), "Window");
            return (Item(menu, "Minimize").Gesture, Item(menu, "Toggle Full Screen").Gesture, Item(menu, "Kurrent Capacitor").Gesture);
        });

        await Assert.That(minimize).IsEqualTo(new KeyGesture(Key.M, KeyModifiers.Meta));
        await Assert.That(fullScreen).IsEqualTo(new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Meta));
        await Assert.That(main).IsEqualTo(new KeyGesture(Key.D0, KeyModifiers.Meta));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Minimize_minimizes_the_window_the_menu_was_built_for() {
        var state = await AvaloniaSession.DispatchAsync(() => {
            var window = new Window();
            Click(Item(Submenu(NewBar().Build(window), "Window"), "Minimize"));
            return window.WindowState;
        });

        await Assert.That(state).IsEqualTo(WindowState.Minimized);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Zoom_toggles_between_maximized_and_normal() {
        var (first, second) = await AvaloniaSession.DispatchAsync(() => {
            var window = new Window();
            var zoom = Item(Submenu(NewBar().Build(window), "Window"), "Zoom");
            Click(zoom);
            var afterFirst = window.WindowState;
            Click(zoom);
            return (afterFirst, window.WindowState);
        });

        await Assert.That(first).IsEqualTo(WindowState.Maximized);
        await Assert.That(second).IsEqualTo(WindowState.Normal);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Toggle_Full_Screen_enters_and_leaves_full_screen() {
        var (first, second) = await AvaloniaSession.DispatchAsync(() => {
            var window = new Window();
            var fullScreen = Item(Submenu(NewBar().Build(window), "Window"), "Toggle Full Screen");
            Click(fullScreen);
            var afterFirst = window.WindowState;
            Click(fullScreen);
            return (afterFirst, window.WindowState);
        });

        await Assert.That(first).IsEqualTo(WindowState.FullScreen);
        await Assert.That(second).IsEqualTo(WindowState.Normal);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_fixed_size_window_cannot_be_zoomed_or_made_full_screen() {
        var (zoom, fullScreen) = await AvaloniaSession.DispatchAsync(() => {
            var menu = Submenu(NewBar().Build(new Window { CanResize = false }), "Window");
            return (Item(menu, "Zoom").IsEnabled, Item(menu, "Toggle Full Screen").IsEnabled);
        });

        await Assert.That(zoom).IsFalse();
        await Assert.That(fullScreen).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_main_window_item_shows_the_main_window() {
        var shown = 0;
        await AvaloniaSession.DispatchAsync(() =>
            Click(Item(Submenu(NewBar(showMainWindow: () => shown++).Build(new Window()), "Window"), "Kurrent Capacitor")));

        await Assert.That(shown).IsEqualTo(1);
    }

    /// A failed startup latches the coordinator but keeps it, and then opens its error window: that
    /// window must not get an item that re-runs the window factory over the torn-down graph.
    [Test]
    public async Task Only_a_coordinator_no_failure_or_quit_has_latched_supplies_the_main_window_item() {
        var live = new MainWindowCoordinator(() => throw new InvalidOperationException("factory"));
        var latched = new MainWindowCoordinator(() => throw new InvalidOperationException("factory")) { QuitInProgress = true };

        await Assert.That(Capacitor.App.App.MainWindowAction(live) is not null).IsTrue();
        await Assert.That(Capacitor.App.App.MainWindowAction(latched) is null).IsTrue();
        await Assert.That(Capacitor.App.App.MainWindowAction(null) is null).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Bring_All_to_Front_activates_every_visible_window_and_leaves_the_invoking_one_key() {
        var order = await AvaloniaSession.DispatchAsync(() => {
            var invoking = new Window { Title = "invoking" };
            var other = new Window { Title = "other" };
            var hidden = new Window { Title = "hidden" };
            try {
                invoking.Show();
                other.Show();
                // Headless windows post Activated to the dispatcher: drain what Show queued before
                // listening, then what the click queued before reading.
                Dispatcher.UIThread.RunJobs();
                var activated = new List<string>();
                foreach (var w in new[] { invoking, other, hidden }) w.Activated += (_, _) => activated.Add(w.Title!);

                Click(Item(Submenu(NewBar(windows: [invoking, other, hidden]).Build(invoking), "Window"), "Bring All to Front"));
                Dispatcher.UIThread.RunJobs();
                return string.Join("|", activated);
            } finally {
                invoking.Close();
                other.Close();
                hidden.Close();
            }
        });

        await Assert.That(order).IsEqualTo("other|invoking");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Help_opens_the_docs_and_the_GitHub_releases() {
        var opener = new RecordingOpener();
        var layout = await AvaloniaSession.DispatchAsync(() => {
            var help = Submenu(NewBar(opener).Build(new Window()), "Help");
            Click(Item(help, "Kurrent Capacitor Documentation"));
            Click(Item(help, "Changelog"));
            return Layout(help);
        });

        await Assert.That(layout).IsEqualTo("Kurrent Capacitor Documentation|Changelog");
        await Assert.That(string.Join("|", opener.Opened))
            .IsEqualTo("https://www.kurrent.io/docs/capacitor/|https://github.com/kurrent-io/kcap-cli/releases");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_help_link_that_fails_to_open_does_not_throw() {
        var opener = new RecordingOpener { ThrowOnOpen = new InvalidOperationException("no browser") };
        await AvaloniaSession.DispatchAsync(() => Click(Item(Submenu(NewBar(opener).Build(new Window()), "Help"), "Changelog")));

        await Assert.That(opener.Opened.Count).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_app_menu_opens_About() {
        var opened = 0;
        var layout = await AvaloniaSession.DispatchAsync(() => {
            var menu = AppMenuBar.BuildAppMenu(() => opened++);
            Click(Item(menu, "About Kurrent Capacitor"));
            return Layout(menu);
        });

        await Assert.That(layout).IsEqualTo("About Kurrent Capacitor");
        await Assert.That(opened).IsEqualTo(1);
    }

    /// Without an app menu of its own, Avalonia substitutes one whose only item is "About Avalonia".
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_app_brings_its_own_app_menu() {
        var layout = await AvaloniaSession.DispatchAsync(() => Layout(NativeMenu.GetMenu(Avalonia.Application.Current!)!));

        await Assert.That(layout).IsEqualTo("About Kurrent Capacitor");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Attach_keeps_the_menu_a_window_already_has() {
        var same = await AvaloniaSession.DispatchAsync(() => {
            var window = new Window();
            var bar = NewBar();
            bar.Attach(window);
            var first = NativeMenu.GetMenu(window);
            bar.Attach(window);
            return first is not null && ReferenceEquals(first, NativeMenu.GetMenu(window));
        });

        await Assert.That(same).IsTrue();
    }
}

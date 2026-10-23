using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class WorkspaceSwitchCoverTests {
    /// On a 2x display the still the cover holds over a session switch reproduces the pane pixel for
    /// pixel: not its top-left quarter magnified, nor a 1x capture scaled up. The outgoing content is
    /// hidden once the still is taken, so what the frame still shows can only come from the cover.
    /// The hold's limit runs on the view model's clock, so the sample cannot race it, and the cover
    /// lifts once that clock passes the limit.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_switch_cover_shows_the_outgoing_pane_unmagnified_at_2x() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var sampled = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                var actions = NewActions();
                var time = new FakeTimeProvider();
                var vm = new MainWindowViewModel(
                    service, CancellationToken.None, TestActivity.New(), time,
                    workspaceFactory: agentId => new WorkspaceViewModel(
                        agentId, service, actions, new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(),
                        new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(),
                        new ScriptedLocalControlOps(), new NoAttachmentUploader()));
                var window = new MainWindow { DataContext = vm };
                window.Show();
                window.SetRenderScaling(2);
                try {
                    var pane = Named<Panel>(window, "ContentPane");
                    var cover = Named<Control>(window, "SwitchCover");
                    var outgoing = Quadrants();
                    pane.Children.Insert(pane.Children.IndexOf(cover), outgoing);
                    Dispatcher.UIThread.RunJobs();
                    var area = new Rect(pane.TranslatePoint(default, window)!.Value, pane.Bounds.Size);
                    var before = Pixels(window.CaptureRenderedFrame()!, area);

                    vm.OpenSession("0123456789abcdef0123456789abcdef");
                    outgoing.IsVisible = false;
                    Dispatcher.UIThread.RunJobs();
                    var during = Pixels(window.CaptureRenderedFrame()!, area);
                    var covered = cover.IsVisible;

                    time.Advance(TimeSpan.FromMilliseconds(400));
                    Dispatcher.UIThread.RunJobs();
                    return (before, during, covered, lifted: !cover.IsVisible);
                } finally {
                    vm.CloseWorkspace();
                    window.Close();
                    Dispatcher.UIThread.RunJobs();
                }
            });

            await Assert.That(sampled.covered).IsTrue();
            await Assert.That(sampled.before.Distinct().Count()).IsGreaterThanOrEqualTo(4);
            await Assert.That(sampled.during.Where((pixel, i) => pixel != sampled.before[i]).Count()).IsEqualTo(0);
            await Assert.That(sampled.lifted).IsTrue();
        });
    }

    static T Named<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    static Grid Quadrants() {
        var grid = new Grid { RowDefinitions = new("*,*"), ColumnDefinitions = new("*,*") };
        IBrush[] fills = [Brushes.Red, Brushes.Lime, Brushes.Blue, Brushes.Yellow];
        for (var i = 0; i < fills.Length; i++) {
            var cell = new Border { Background = fills[i] };
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            grid.Children.Add(cell);
        }
        return grid;
    }

    static int[] Pixels(WriteableBitmap frame, Rect area) {
        var rect = PixelRect.FromRect(area, frame.Dpi.X / 96).Intersect(new PixelRect(frame.PixelSize));
        using var buffer = frame.Lock();
        var pixels = new int[rect.Width * rect.Height];
        for (var row = 0; row < rect.Height; row++)
            Marshal.Copy(buffer.Address + (rect.Y + row) * buffer.RowBytes + rect.X * 4, pixels, row * rect.Width, rect.Width);
        return pixels;
    }
}

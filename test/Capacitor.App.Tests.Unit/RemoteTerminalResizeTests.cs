using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class RemoteTerminalResizeTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_scrollbar_tracks_history_ownership_and_detaches_from_a_replaced_model() {
        await RunOnUiAsync(async () => {
            var upstream = new SvcSystems.UI.Terminal.TerminalControl();
            await Assert.That(upstream.ColumnDefinitions.Count).IsEqualTo(2);
            await Assert.That(upstream.RowDefinitions.Count).IsEqualTo(0);
            await Assert.That(Grid.GetColumn(upstream.Children.OfType<ScrollBar>().Single())).IsEqualTo(1);
            var surface = new XtermTerminalSurface(80, 24);
            var control = new RemoteTerminalControl { Model = surface.Model };
            var bar = control.Children.OfType<ScrollBar>().Single();
            for (var i = 0; i < 60; i++) surface.Feed($"line {i}\r\n");
            await Assert.That(bar.Visibility).IsEqualTo(ScrollBarVisibility.Auto);
            await Assert.That(bar.Maximum).IsGreaterThan(0);
            bar.Value = 5;
            await Assert.That(surface.Model.ScrollOffset).IsEqualTo(5);

            surface.Feed("\x1b[?1000h\x1b[?1006h");
            await Assert.That(bar.Visibility).IsEqualTo(ScrollBarVisibility.Hidden);
            await Assert.That(bar.IsHitTestVisible).IsFalse();
            await Assert.That(control.ColumnDefinitions[1].MaxWidth).IsEqualTo(0);
            surface.Feed("another redraw");
            await Assert.That(bar.Visibility).IsEqualTo(ScrollBarVisibility.Hidden);

            surface.Feed("\x1b[?1000l");
            await Assert.That(bar.Visibility).IsEqualTo(ScrollBarVisibility.Auto);
            await Assert.That(bar.IsHitTestVisible).IsTrue();
            surface.Feed("\x1b[?1049h");
            await Assert.That(bar.Visibility).IsEqualTo(ScrollBarVisibility.Hidden);

            var replacement = new XtermTerminalSurface(80, 24);
            control.Model = replacement.Model;
            await Assert.That((object?)surface.Model.UpdateUI).IsNull();
            await Assert.That(bar.Visibility).IsEqualTo(ScrollBarVisibility.Auto);
            replacement.Feed("\x1b[?1000h");
            await Assert.That(bar.Visibility).IsEqualTo(ScrollBarVisibility.Hidden);
            control.Model = null;
            await Assert.That((object?)replacement.Model.UpdateUI).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_window_resize_waits_for_the_source_before_resizing_the_terminal_buffer() {
        await RunOnUiAsync(async () => {
            var surface = new XtermTerminalSurface(130, 54);
            var control = new RemoteTerminalControl {
                SourceColumns = 130, SourceRows = 54,
                FontFamily = new Avalonia.Media.FontFamily("Menlo,Consolas,monospace"),
                Model = surface.Model,
            };
            var requests = new List<(int Cols, int Rows)>();
            control.ViewportChanged += (cols, rows) => requests.Add((cols, rows));
            var viewport = new RemoteTerminalViewport { Content = control };
            var window = new Window { Width = 900, Height = 650, Content = viewport };
            try {
                window.Show();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                for (var i = 0; i < 100; i++) surface.Feed($"line {i}\r\n");
                surface.Feed("prompt");
                var promptLine = surface.Model.Terminal.Buffer.BaseY + surface.Model.Terminal.Buffer.Y;
                requests.Clear();
                foreach (var (width, height) in new[] { (800, 500), (600, 350), (1100, 700) }) {
                    window.Width = width;
                    window.Height = height;
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    await Assert.That(surface.CurrentSize).IsEqualTo((130, 54));
                    await Assert.That(surface.Model.Terminal.Buffer.GetLine(promptLine)!.TranslateToString(true)).IsEqualTo("prompt");
                }
                await Assert.That(requests.Select(size => size.Rows).Distinct().Count()).IsGreaterThan(1);

                surface.Resize(100, 45);
                control.SourceColumns = 100;
                control.SourceRows = 45;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Assert.That(surface.CurrentSize).IsEqualTo((100, 45));
                await Assert.That(surface.Model.Terminal.Buffer.GetLine(promptLine)!.TranslateToString(true)).IsEqualTo("prompt");
            } finally {
                window.Close();
            }
        });
    }
}

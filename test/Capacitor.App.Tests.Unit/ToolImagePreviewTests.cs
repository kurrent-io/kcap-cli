using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class ToolImagePreviewTests {
    [Test]
    public async Task Relative_paths_require_a_session_root() {
        using var tmp = new TempDir();
        await Assert.That(ToolDetail.ResolvePreviewPath("images/a.png", tmp.Path))
            .IsEqualTo(tmp.PathTo("images", "a.png"));
        await Assert.That(ToolDetail.ResolvePreviewPath("images/../images/a.png", tmp.Path))
            .IsEqualTo(tmp.PathTo("images", "a.png"));
        await Assert.That(ToolDetail.ResolvePreviewPath(tmp.PathTo("a.png"), "/other"))
            .IsEqualTo(tmp.PathTo("a.png"));
        await Assert.That(ToolDetail.ResolvePreviewPath("a.png", null)).IsNull();
        await Assert.That(ToolDetail.ResolvePreviewPath(null, tmp.Path)).IsNull();
    }

    [Test]
    public async Task The_read_limit_is_enforced_on_consumed_bytes() {
        using var exact = new MemoryStream(new byte[ToolPreviewImages.MaxBytes]);
        using var accepted = ToolPreviewImages.ReadBounded(exact);
        await Assert.That(accepted!.Length).IsEqualTo(ToolPreviewImages.MaxBytes);
        await Assert.That(accepted.Position).IsEqualTo(0);
        using var growing = new MemoryStream(new byte[ToolPreviewImages.MaxBytes + 1]);
        using var rejected = ToolPreviewImages.ReadBounded(growing);
        await Assert.That(rejected).IsNull();
    }

    [Test]
    public async Task Preview_renders_toggles_and_discards_stale_loads() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("a.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"40\"><rect width=\"40\" height=\"40\" fill=\"red\"/></svg>");
        await AvaloniaSession.RunOnUiAsync(async () => {
            var preview = new ToolImagePreview { Path = path };
            preview.Classes.Add("kcapGhost");
            var window = new Window { Content = preview, Width = 300, Height = 250 };
            window.Show();
            try {
                await preview.Loading;
                await Assert.That(preview.IsVisible).IsTrue();
                await Assert.That(preview.Unavailable).IsFalse();
                var image = (Image)preview.Content!;
                await Assert.That(image.Source).IsNotNull();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                await Assert.That(image.Bounds.Width > 0).IsTrue();
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Assert.That(image.MaxHeight).IsEqualTo(520);
                preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Assert.That(image.MaxHeight).IsEqualTo(140);

                preview.Path = null;
                preview.Path = path;
                var pending = preview.Loading;
                preview.Path = tmp.PathTo("missing.png");
                await pending;
                await preview.Loading;
                await Assert.That(preview.IsVisible).IsFalse();
                await Assert.That(preview.Unavailable).IsTrue();
                await Assert.That(image.Source).IsNull();

                preview.Path = path;
                await preview.Loading;
                window.Content = null;
                await Assert.That(image.Source).IsNull();
            } finally {
                window.Close();
                ToolPreviewImages.Clear();
            }
        });
    }

    [Test]
    public async Task Cache_reuses_unchanged_images_and_preserves_active_leases() {
        using var tmp = new TempDir();
        var path = tmp.CreateFile("cached.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2\" height=\"2\"/>");
        try {
            using var first = await Task.Run(() => ToolPreviewImages.Acquire(path));
            using var second = await Task.Run(() => ToolPreviewImages.Acquire(path));
            await Assert.That(first).IsNotNull();
            await Assert.That(ReferenceEquals(first, second)).IsTrue();
            ToolPreviewImages.Clear();
            await AvaloniaSession.RunOnUiAsync(async () => {
                await Assert.That(first!.Image.Size.Width).IsEqualTo(2);
            });
        } finally {
            ToolPreviewImages.Clear();
        }
    }
}

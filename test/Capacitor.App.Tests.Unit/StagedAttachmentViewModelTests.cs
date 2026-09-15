using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The chip's thumbnail without a view in sight: only an image is decoded at all, a decode the
/// codec refuses leaves the glyph, and one that lands after the chip is gone is never published.
public class StagedAttachmentViewModelTests {
    const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    static StagedAttachmentViewModel Chip(string name, string contentType, byte[] bytes) =>
        new(new StagedAttachment(name, contentType, bytes), _ => { });

    static StagedAttachmentViewModel RefusedChip(string name) =>
        new(new StagedAttachment(name, "image/png", Convert.FromBase64String(OnePixelPng)), _ => { },
            _ => throw new InvalidOperationException("this is not an image"));

    /// The content type decides, not the bytes: the text chip here carries a real PNG.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_image_decodes_a_thumbnail_and_nothing_else_is_even_attempted() {
        await RunOnUiAsync(async () => {
            using var image = Chip("a.png", "image/png", Convert.FromBase64String(OnePixelPng));
            await image.PendingThumbnailForTesting!;
            await Assert.That(image.Thumbnail).IsNotNull();

            using var text = Chip("notes.txt", "text/plain", Convert.FromBase64String(OnePixelPng));
            await Assert.That(text.PendingThumbnailForTesting is null).IsTrue();
            await Assert.That(text.Thumbnail).IsNull();
            await Assert.That(text.Glyph).IsEqualTo("TXT");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_decode_leaves_the_chip_on_its_glyph() {
        await RunOnUiAsync(async () => {
            using var chip = RefusedChip("broken.png");
            await chip.PendingThumbnailForTesting!;
            await Assert.That(chip.Thumbnail).IsNull();
            await Assert.That(chip.Glyph).IsEqualTo("PNG");
        });
    }

    /// Dispose lands while the decode is still on its worker: publishing needs the UI thread, and
    /// this test holds it until the await below, so the bitmap has nowhere to land but the bin.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_decode_landing_after_dispose_is_never_published() {
        await RunOnUiAsync(async () => {
            var chip = Chip("late.png", "image/png", Convert.FromBase64String(OnePixelPng));
            chip.Dispose();

            await chip.PendingThumbnailForTesting!;

            await Assert.That(chip.Thumbnail).IsNull();
        });
    }
}

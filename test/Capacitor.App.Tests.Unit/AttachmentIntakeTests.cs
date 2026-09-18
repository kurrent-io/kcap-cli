using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Capacitor.App.Tests.Unit;

public class AttachmentIntakeTests {
    [Test]
    public async Task Classify_prefers_files_then_text_then_bitmap() {
        await Assert.That(AttachmentIntake.Classify([DataFormat.File, DataFormat.Text, DataFormat.Bitmap], true)).IsEqualTo(IntakeKind.Files);
        await Assert.That(AttachmentIntake.Classify([DataFormat.Text, DataFormat.Bitmap], true)).IsEqualTo(IntakeKind.Text);
        await Assert.That(AttachmentIntake.Classify([DataFormat.Text, DataFormat.Bitmap], false)).IsEqualTo(IntakeKind.Bitmap);
        await Assert.That(AttachmentIntake.Classify([DataFormat.Bitmap], false)).IsEqualTo(IntakeKind.Bitmap);
        await Assert.That(AttachmentIntake.Classify([], false)).IsEqualTo(IntakeKind.Nothing);
    }

    [Test]
    public async Task Read_files_refuses_each_bad_item_by_name_and_keeps_the_valid_sibling_after_it() {
        var bigBin = FakeStorageFile.Of("big.bin", reportedSize: InputWire.MaxAttachmentBytes + 1);
        var aPngStream = new FakeStorageFile.TrackedStream(new byte[3]);
        var items = new IStorageItem[] {
            FakeStorageFolder.Of("Docs"),
            FakeStorageFile.Of("a.png", aPngStream),
            bigBin,
            FakeStorageFile.Of("b.txt", new byte[2]),
            FakeStorageFile.Of("nosize.bin", new byte[InputWire.MaxAttachmentBytes + 1], reportedSize: null),
            FakeStorageFile.Of("c.md", new byte[1]),
            FakeStorageFile.Of("locked.txt", openThrows: new UnauthorizedAccessException()),
            FakeStorageFile.Of("d.json", new byte[1]),
            FakeStorageFile.Of("half.bin", new byte[100], throwAfterBytes: 50),
            FakeStorageFile.Of("e.csv", new byte[1]),
        };
        var result = await AttachmentIntake.ReadFilesAsync(items, InputWire.MaxAttachmentsPerPrompt, CancellationToken.None);
        await Assert.That(result.Accepted.Select(f => f.FileName)).IsEquivalentTo(["a.png", "b.txt", "c.md", "d.json", "e.csv"]);
        await Assert.That(result.Refused).IsEquivalentTo([
            new IntakeRefusal("Docs", "is a folder"), new IntakeRefusal("big.bin", AttachmentTray.SizeReason),
            new IntakeRefusal("nosize.bin", AttachmentTray.SizeReason), new IntakeRefusal("locked.txt", "could not be read"),
            new IntakeRefusal("half.bin", "could not be read")]);
        _ = bigBin.DidNotReceive().OpenReadAsync();
        await Assert.That(result.Accepted[0].ContentType).IsEqualTo("image/png");
        await Assert.That(aPngStream.Disposed).IsTrue();
    }

    /// The third file's fake throws if opened, so a refusal by cap rather than "could not be read" is
    /// the proof that nothing past the free slots is read.
    [Test]
    public async Task Read_files_opens_nothing_past_the_free_slots_and_names_the_rest() {
        var items = new IStorageItem[] {
            FakeStorageFile.Of("a.png", new byte[1]), FakeStorageFile.Of("b.png", new byte[1]),
            FakeStorageFile.Of("c.png", reportedSize: 1), FakeStorageFolder.Of("Docs"),
        };
        var result = await AttachmentIntake.ReadFilesAsync(items, 2, CancellationToken.None);
        await Assert.That(result.Accepted.Select(f => f.FileName)).IsEquivalentTo(["a.png", "b.png"]);
        await Assert.That(result.Refused).IsEquivalentTo([new IntakeRefusal("c.png", AttachmentTray.CapReason), new IntakeRefusal("Docs", "is a folder")]);
    }

    [Test]
    public async Task Read_files_propagates_the_callers_cancellation() {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => AttachmentIntake.ReadFilesAsync([FakeStorageFile.Of("a.png", new byte[1])], InputWire.MaxAttachmentsPerPrompt, cts.Token));
    }

    [Test]
    public async Task Content_types_come_from_the_extension_table() {
        await Assert.That(AttachmentIntake.ContentTypeFor("x.PNG")).IsEqualTo("image/png");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.jpg")).IsEqualTo("image/jpeg");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.pdf")).IsEqualTo("application/pdf");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.cs")).IsEqualTo("text/plain");
        await Assert.That(AttachmentIntake.ContentTypeFor("x.unknownext")).IsEqualTo("application/octet-stream");
    }

    /// The headless test session has no real image codec, so this only pins the clock-based name
    /// and the content type FromBitmap hands to the FromPngBytes seam — not the encoded bytes,
    /// which the oversize-refusal test below covers via that seam directly.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task From_bitmap_names_the_file_by_the_clock_and_tags_it_as_png() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 10, 30, 5, TimeSpan.Zero));
            using var small = new WriteableBitmap(new Avalonia.PixelSize(4, 4), new Avalonia.Vector(96, 96));
            var ok = AttachmentIntake.FromBitmap(small, time);
            await Assert.That(ok.Accepted.Single().FileName).IsEqualTo("pasted-image-20260914-103005.png");
            await Assert.That(ok.Accepted.Single().ContentType).IsEqualTo("image/png");
        });
    }

    [Test]
    public async Task From_png_bytes_refuses_an_oversize_encoding() {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 14, 10, 30, 5, TimeSpan.Zero));
        var oversize = new byte[11 * 1024 * 1024];
        var result = AttachmentIntake.FromPngBytes(oversize, time);
        await Assert.That(result.Accepted).IsEmpty();
        await Assert.That(result.Refused).IsEquivalentTo([new IntakeRefusal("pasted-image-20260914-103005.png", AttachmentTray.SizeReason)]);
    }
}

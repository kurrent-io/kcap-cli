using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

public class AttachmentTrayTests {
    static StagedAttachment File(string name, int size = 3) => new(name, "image/png", new byte[size]);

    [Test]
    public async Task Add_all_refuses_oversize_and_over_count_with_the_stated_wording_and_stages_the_rest() {
        var tray = new AttachmentTray();
        var big = new StagedAttachment("report.zip", "application/zip", new byte[InputWire.MaxAttachmentBytes + 1]);
        var refused = tray.AddAll([File("a.png"), big, File("b.png")]);
        await Assert.That(tray.Items.Select(f => f.FileName)).IsEquivalentTo(["a.png", "b.png"]);
        await Assert.That(refused).IsEquivalentTo([new IntakeRefusal("report.zip", AttachmentTray.SizeReason)]);

        var many = Enumerable.Range(0, 10).Select(i => File($"f{i}.png")).ToList();
        refused = tray.AddAll(many);
        await Assert.That(tray.Count).IsEqualTo(10);
        await Assert.That(refused.Select(r => r.Reason).Distinct()).IsEquivalentTo(["only 10 files per message"]);
        await Assert.That(refused.Select(r => r.Name)).IsEquivalentTo(["f8.png", "f9.png"]);
    }

    [Test]
    public async Task Duplicate_names_get_a_numbered_suffix_before_the_extension() {
        var tray = new AttachmentTray();
        tray.AddAll([File("shot.png"), File("shot.png"), File("shot.png")]);
        await Assert.That(tray.Items.Select(f => f.FileName)).IsEquivalentTo(["shot.png", "shot (2).png", "shot (3).png"]);
    }

    /// Deduplication renames the chip the caller already holds a receipt for, so a rename that
    /// minted a fresh id would leave RemoveAll unable to clear what the send delivered.
    [Test]
    public async Task Renaming_a_chip_keeps_its_id() {
        var file = File("shot.png");
        var renamed = file.Renamed("shot (2).png");
        await Assert.That(renamed.Id).IsEqualTo(file.Id);
        await Assert.That(renamed.FileName).IsEqualTo("shot (2).png");
    }

    [Test]
    public async Task Remove_all_removes_exactly_the_given_ids_and_generation_counts_real_mutations() {
        var tray = new AttachmentTray();
        var a = File("a.png"); var b = File("b.png"); var c = File("c.png");
        tray.AddAll([a, b]);
        var snapshot = tray.Snapshot();
        var g0 = tray.Generation;
        tray.AddAll([c]);            // added mid-flight
        tray.Remove(b);              // removed mid-flight
        tray.RemoveAll(snapshot.Select(f => f.Id).ToList());
        await Assert.That(tray.Items).IsEquivalentTo([c]);
        await Assert.That(tray.Generation).IsGreaterThan(g0);
        var g1 = tray.Generation;
        tray.RemoveAll([a.Id]);      // nothing to remove
        await Assert.That(tray.Generation).IsEqualTo(g1);
    }

    [Test]
    public async Task Snapshot_is_a_copy_and_restore_replaces_contents() {
        var tray = new AttachmentTray();
        tray.AddAll([File("a.png")]);
        var snapshot = tray.Snapshot();
        tray.Clear();
        await Assert.That(tray.Count).IsEqualTo(0);
        await Assert.That(snapshot).Count().IsEqualTo(1);
        tray.AddAll([File("z.png")]);
        tray.Restore(snapshot);
        await Assert.That(tray.Items.Select(f => f.FileName)).IsEquivalentTo(["a.png"]);
    }

    [Test]
    public async Task Removed_chip_bytes_are_unreferenced_while_a_receipt_still_holds_its_id() {
        var tray = new AttachmentTray();
        WeakReference weak = Stage(tray, out var id);
        tray.RemoveAll([id]);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Assert.That(weak.IsAlive).IsFalse();
        await Assert.That(id).IsNotEqualTo(Guid.Empty);

        static WeakReference Stage(AttachmentTray tray, out Guid id) {
            var f = new StagedAttachment("x.bin", "application/octet-stream", new byte[1024]);
            tray.AddAll([f]);
            id = f.Id;
            return new WeakReference(f);
        }
    }

    [Test]
    public async Task Size_label_and_image_flag() {
        await Assert.That(new StagedAttachment("a", "image/png", new byte[512]).SizeLabel).IsEqualTo("512 B");
        await Assert.That(new StagedAttachment("a", "image/png", new byte[184 * 1024]).SizeLabel).IsEqualTo("184 KB");
        await Assert.That(new StagedAttachment("a", "text/plain", new byte[(int)(2.3 * 1024 * 1024)]).SizeLabel).IsEqualTo("2.3 MB");
        await Assert.That(new StagedAttachment("a", "text/plain", new byte[1]).IsImage).IsFalse();
    }
}

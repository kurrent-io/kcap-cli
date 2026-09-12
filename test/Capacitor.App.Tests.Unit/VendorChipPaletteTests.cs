using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class VendorChipPaletteTests {
    [Test]
    public async Task Known_vendor_gets_its_web_colour() {
        var claude = VendorChipPalette.For("claude");
        await Assert.That(claude.Background).IsEqualTo("#C87B3A");
        await Assert.That(claude.Foreground).IsEqualTo("#1E1E1E");
    }

    [Test]
    public async Task Vendor_lookup_is_case_insensitive() {
        await Assert.That(VendorChipPalette.For("Codex")).IsEqualTo(VendorChipPalette.For("codex"));
    }

    [Test]
    public async Task Cursor_and_unknown_fall_back_to_the_neutral_pair() {
        var cursor = VendorChipPalette.For("cursor");
        var unknown = VendorChipPalette.For("something-else");
        var missing = VendorChipPalette.For(null);
        await Assert.That(cursor).IsEqualTo(unknown);
        await Assert.That(unknown).IsEqualTo(missing);
        await Assert.That(cursor.Background).IsEqualTo("#191D27");
    }
}

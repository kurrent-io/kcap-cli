using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit;

public class ShortcutLabelsTests {
    [Test]
    public async Task Hints_use_the_command_glyph_on_macos_and_ctrl_elsewhere() {
        await Assert.That(ShortcutLabels.For("N", isMacOs: true)).IsEqualTo("⌘N");
        await Assert.That(ShortcutLabels.For("N", isMacOs: false)).IsEqualTo("Ctrl+N");
        await Assert.That(ShortcutLabels.NewSession).IsEqualTo(ShortcutLabels.For("N", OperatingSystem.IsMacOS()));
    }
}

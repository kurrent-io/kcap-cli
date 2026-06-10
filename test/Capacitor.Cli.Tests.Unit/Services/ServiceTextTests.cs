using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTextTests {
    [Test]
    public async Task ServiceId_sanitizes_and_lowercases() {
        await Assert.That(ServiceText.ServiceId("My Laptop")).IsEqualTo("my-laptop");
    }

    [Test]
    public async Task ServiceId_is_idempotent() {
        var once = ServiceText.ServiceId("a/b c");
        await Assert.That(ServiceText.ServiceId(once)).IsEqualTo(once);
    }

    [Test]
    public async Task Xml_escapes_the_five_markup_chars() {
        await Assert.That(ServiceText.Xml("a&b<c>\"d'")).IsEqualTo("a&amp;b&lt;c&gt;&quot;d&apos;");
    }

    [Test]
    public async Task CmdValue_doubles_percent_signs() {
        await Assert.That(ServiceText.CmdValue("100%PATH%")).IsEqualTo("100%%PATH%%");
    }

    [Test]
    public async Task SystemdValue_collapses_newlines_to_spaces() {
        await Assert.That(ServiceText.SystemdValue("a\nb")).IsEqualTo("a b");
    }
}

using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class ImageLabelTests {
    [Test]
    [Arguments("Remediation recommended", "https://img.shields.io/badge/Medium-634FD1", "Remediation recommended")]
    [Arguments("  two\n words ", "https://h/x.png", "two words")]
    [Arguments("", "https://h/path/name.png?x=1#f", "name.png")]
    [Arguments(null, "https://h/path/name.png", "name.png")]
    [Arguments("   ", "https://h/dir/", "dir")]
    [Arguments("", "relative/shot.png", "shot.png")]
    [Arguments("", "", "image")]
    [Arguments(null, null, "image")]
    [Arguments("", "https://h/", "image")]
    [Arguments("", "data:image/gif;base64,R0lGODlhAQABAAAAACw=", "image")]
    [Arguments(null, " DATA:image/png;base64,iVBORw0KGgo=", "image")]
    public async Task The_label_is_the_alt_then_the_file_name_then_a_word(string? alt, string? url, string expected) =>
        await Assert.That(ImageLabel.For(alt, url)).IsEqualTo(expected);

    [Test]
    public async Task A_file_name_longer_than_any_filesystem_allows_reads_as_a_word() {
        await Assert.That(ImageLabel.For("", "https://h/" + new string('a', 255))).IsEqualTo(new string('a', 255));
        await Assert.That(ImageLabel.For("", "https://h/" + new string('a', 256))).IsEqualTo("image");
    }

    /// Pins normalisation coming last: unescaping a file name can produce a line end, and a line
    /// end inside a run hangs layout.
    [Test]
    [Arguments("https://h/a%0Ab.png", "a b.png")]
    [Arguments("https://h/a%0D%0Ab.png", "a b.png")]
    [Arguments("https://h/%20%0A%20", "image")]
    public async Task An_unescaped_file_name_never_carries_a_line_end(string url, string expected) {
        var label = ImageLabel.For("", url);
        await Assert.That(label).IsEqualTo(expected);
        await Assert.That(label.Contains('\r') || label.Contains('\n')).IsFalse();
    }
}

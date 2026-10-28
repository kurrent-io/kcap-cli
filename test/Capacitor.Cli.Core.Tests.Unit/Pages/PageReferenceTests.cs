using Capacitor.Cli.Core.Pages;

namespace Capacitor.Cli.Core.Tests.Unit.Pages;

public class PageReferenceTests {
    [Test]
    [Arguments("art_01J9", "art_01J9")]
    [Arguments("  art_01J9 ", "art_01J9")]
    [Arguments("https://kcap.test/pages/art_01J9", "art_01J9")]
    [Arguments("https://kcap.test/artefacts/art_01J9", "art_01J9")]
    [Arguments("https://kcap.test/artefacts/art_01J9/content?v=2", "art_01J9")]
    [Arguments("https://kcap.test/pages/art_01J9?theme=dark#top", "art_01J9")]
    [Arguments("https://kcap.test/pages/a%20b", null)]
    [Arguments("https://kcap.test/sessions/abc", null)]
    [Arguments("https://kcap.test/pages", null)]
    [Arguments("..", null)]
    [Arguments("a/b", null)]
    [Arguments("a\\b", null)]
    [Arguments("", null)]
    [Arguments(null, null)]
    public async Task A_page_is_named_by_its_id_or_any_link_to_it(string? reference, string? expected) =>
        await Assert.That(PageReference.ParseId(reference)).IsEqualTo(expected);
}

using Avalonia.Threading;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

/// GitHub's references in a body link into the repository the view is given, and only in its text:
/// a code span, a fenced block, an email and an existing link's label stay as they are.
public class ReferenceLinksTests {
    const string Repository = "https://github.com/acme/widgets";

    static async Task<List<(string Label, string Target)>> LinksIn(string markdown, string? repository) {
        var (window, view, _) = Show(markdown, MarkdownFlavor.GitHub);
        try {
            view.ReferenceBase = repository;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return AllLinks(view).Select(l => (l.Link.Inlines.Text ?? "", l.Link.NavigateUri!.AbsoluteUri)).ToList();
        } finally {
            window.Close();
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("Fixes #12.", "#12", Repository + "/issues/12")]
    [Arguments("See acme/other#3", "acme/other#3", "https://github.com/acme/other/issues/3")]
    [Arguments("Thanks @octo-cat!", "@octo-cat", "https://github.com/octo-cat")]
    [Arguments("Landed in 1a2b3c4d.", "1a2b3c4d", Repository + "/commit/1a2b3c4d")]
    [Arguments("Landed in 1A2B3C4D.", "1A2B3C4D", Repository + "/commit/1a2b3c4d")]
    [Arguments("**Fixes #12**", "#12", Repository + "/issues/12")]
    public async Task A_reference_in_text_links_into_the_repository(string markdown, string label, string target) {
        await RunOnUiAsync(async () => {
            var links = await LinksIn(markdown, Repository);
            await Assert.That(links).IsEquivalentTo(new[] { (label, target) });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("Not `#5` or `1a2b3c4`.")]
    [Arguments("```\n#7 @octo 1a2b3c4\n```")]
    [Arguments("Mail a@b.com about 1234567 or deadbee.")]
    [Arguments("No space#8 and no x/y/z#9.")]
    [Arguments("Not #0, acme/x#0, acme/..#1, acme/...#1 or acme/.#1.")]
    [Arguments("Escaped \\#12 and \\@octo stay text.")]
    public async Task Code_emails_and_lookalikes_stay_text(string markdown) {
        await RunOnUiAsync(async () => {
            var links = await LinksIn(markdown, Repository);
            await Assert.That(links).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_existing_link_keeps_its_label_unlinked_inside() {
        await RunOnUiAsync(async () => {
            var links = await LinksIn("[fixes #6](https://x.example/)", Repository);
            await Assert.That(links).IsEquivalentTo(new[] { ("fixes #6", "https://x.example/") });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Without_a_repository_nothing_is_linked() {
        await RunOnUiAsync(async () => {
            var links = await LinksIn("Fixes #12 by @octo in 1a2b3c4d", repository: null);
            await Assert.That(links).IsEmpty();
        });
    }
}

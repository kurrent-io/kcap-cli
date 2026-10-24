using Capacitor.Cli.Core.LocalIpc;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class AttachmentTrailerTests {
    [Test]
    public async Task Shapes_one_two_and_many_paths_behind_the_prefix() {
        await Assert.That(AttachmentTrailer.For([".attached/b1/a.png"])).IsEqualTo("[Attached files: .attached/b1/a.png]");
        await Assert.That(AttachmentTrailer.For(["/x/a.png", "/x/b.pdf"])).IsEqualTo("[Attached files: /x/a.png, /x/b.pdf]");
        await Assert.That(AttachmentTrailer.For(["a", "b", "c"])).StartsWith(AttachmentTrailer.Prefix);
        await Assert.That(AttachmentTrailer.Prefix).IsEqualTo("[Attached files: ");
    }

    [Test]
    [Arguments("look at this  \n\n[Attached files: /x/a.png]", "look at this")]
    [Arguments("[Attached files: /x/a.png]", "")]
    [Arguments("no files here", "no files here")]
    [Arguments("quote:\n\n[Attached files: is how it starts", "quote:\n\n[Attached files: is how it starts")]
    [Arguments("[Attached files: typed by hand\nand more", "[Attached files: typed by hand\nand more")]
    public async Task Strip_leaves_the_typed_prompt(string text, string typed) {
        await Assert.That(AttachmentTrailer.Strip(text)).IsEqualTo(typed);
    }

    [Test]
    public async Task Split_names_each_path_the_trailer_carries() {
        var (typed, paths) = AttachmentTrailer.Split("look\n\n" + AttachmentTrailer.For([".attached/b1/a.png", "/x/b c.pdf"]));
        await Assert.That(typed).IsEqualTo("look");
        await Assert.That(paths).IsEquivalentTo(new[] { ".attached/b1/a.png", "/x/b c.pdf" }, CollectionOrdering.Matching);

        await Assert.That(AttachmentTrailer.Split("[Attached files: /x/a.png]").Paths).IsEquivalentTo(new[] { "/x/a.png" });
        await Assert.That(AttachmentTrailer.Split("no files here").Paths).IsEmpty();
    }
}

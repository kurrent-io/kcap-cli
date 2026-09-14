using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class AttachmentTrailerTests {
    [Test]
    public async Task Shapes_one_two_and_many_paths_behind_the_prefix() {
        await Assert.That(AttachmentTrailer.For([".attached/b1/a.png"])).IsEqualTo("[Attached files: .attached/b1/a.png]");
        await Assert.That(AttachmentTrailer.For(["/x/a.png", "/x/b.pdf"])).IsEqualTo("[Attached files: /x/a.png, /x/b.pdf]");
        await Assert.That(AttachmentTrailer.For(["a", "b", "c"])).StartsWith(AttachmentTrailer.Prefix);
        await Assert.That(AttachmentTrailer.Prefix).IsEqualTo("[Attached files: ");
    }
}

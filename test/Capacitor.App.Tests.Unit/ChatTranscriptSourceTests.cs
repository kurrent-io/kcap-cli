using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class ChatTranscriptSourceTests {
    [Test]
    public async Task Formats_pick_the_reader() {
        var (vendorProjection, vendorNote) = ChatTranscriptSource.Resolve(Agent("a", "claude", hasTerminal: true) with { TranscriptFormat = TranscriptFormats.Vendor });
        await Assert.That(vendorProjection).IsNotNull();
        await Assert.That(vendorNote).IsNull();

        var (leafless, leaflessNote) = ChatTranscriptSource.Resolve(Agent("a", "gemini", hasTerminal: true) with { TranscriptFormat = TranscriptFormats.Vendor });
        await Assert.That(leafless).IsNull();
        await Assert.That(leaflessNote).IsNull();

        var (journal, journalNote) = ChatTranscriptSource.Resolve(Agent("a", "pi", hasTerminal: false) with { TranscriptFormat = TranscriptFormats.Envelopes });
        await Assert.That(journal).IsSameReferenceAs(TranscriptChat.Journal);
        await Assert.That(journalNote).IsNull();
    }

    [Test]
    public async Task Null_format_is_the_older_daemon_for_a_non_pty_dto_and_the_vendor_path_for_a_pty_dto() {
        var (older, olderNote) = ChatTranscriptSource.Resolve(Agent("a", "pi", hasTerminal: false));
        await Assert.That(older).IsNull();
        await Assert.That(olderNote).IsEqualTo("Update the daemon to view this session");

        var (pty, ptyNote) = ChatTranscriptSource.Resolve(Agent("a", "claude", hasTerminal: true));
        await Assert.That(pty).IsNotNull();
        await Assert.That(ptyNote).IsNull();
    }

    [Test]
    public async Task Unknown_format_asks_for_an_app_update() {
        var (projection, note) = ChatTranscriptSource.Resolve(Agent("a", "pi", hasTerminal: false) with { TranscriptFormat = "v9" });
        await Assert.That(projection).IsNull();
        await Assert.That(note).IsEqualTo("Update the app to view this session");
    }
}

using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class ListFrameCodecTests {
    /// A plain List must stay a zero-length payload: that is the only shape an older daemon sent
    /// and the only request an older client makes.
    [Test]
    public async Task Plain_list_encodes_an_empty_payload() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, new LocalFrame(FrameType.List), default);

        await Assert.That(ms.Length).IsEqualTo(5);
    }

    [Test]
    public async Task List_with_titles_round_trips_its_request() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.ListWithTitles(), default);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, default);

        await Assert.That(f!.Type).IsEqualTo(FrameType.List);
        await Assert.That(f.Text).IsEqualTo(LocalFrame.ListTitleColumn);
    }
}

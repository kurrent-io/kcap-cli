using Capacitor.Cli.Capture;

namespace Capacitor.Cli.Tests.Unit.Capture;

public class BoundedJsonBufferWriterTests {
    [Test]
    public async Task RefusesReservationsAndAdvancesPastLimit() {
        using var writer = new BoundedJsonBufferWriter(8);
        writer.GetSpan(5).Fill(42);
        writer.Advance(5);
        await Assert.That(() => writer.GetMemory(4)).Throws<RedactionOutputLimitException>();
        await Assert.That(() => writer.Advance(4)).Throws<RedactionOutputLimitException>();
        await Assert.That(writer.WrittenMemory.Length).IsEqualTo(5);
        writer.GetSpan(3).Fill(43);
        writer.Advance(3);
        await Assert.That(writer.WrittenMemory.ToArray()).IsEquivalentTo(new byte[] {42, 42, 42, 42, 42, 43, 43, 43});
    }
}

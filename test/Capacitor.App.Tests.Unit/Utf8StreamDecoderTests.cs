using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class Utf8StreamDecoderTests {
    [Test]
    [Arguments("é")]      // 2-byte
    [Arguments("€")]      // 3-byte
    [Arguments("𝄞")]      // 4-byte
    public async Task Multibyte_characters_split_at_every_boundary_reassemble(string ch) {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"a{ch}b");
        for (var split = 1; split < bytes.Length; split++) {
            var decoder = new Utf8StreamDecoder();
            var text = decoder.Decode(bytes.AsSpan(0, split)) + decoder.Decode(bytes.AsSpan(split)) + decoder.Flush();
            await Assert.That(text).IsEqualTo($"a{ch}b");
        }
    }

    [Test]
    public async Task The_snapshot_live_seam_is_one_stream() {
        var bytes = System.Text.Encoding.UTF8.GetBytes("€");
        var decoder = new Utf8StreamDecoder();
        var snapshotPart = decoder.Decode(bytes.AsSpan(0, 1));   // snapshot ends mid-character
        var livePart = decoder.Decode(bytes.AsSpan(1));          // first live frame completes it
        await Assert.That(snapshotPart + livePart).IsEqualTo("€");
    }

    /// Pins that a replay decoded in pieces reads as one stream across piece seams (3-byte
    /// characters straddle them) and that no piece reaches the large-object heap.
    [Test]
    public async Task A_replay_decodes_in_pieces_that_stay_off_the_large_object_heap() {
        var replay = string.Concat(Enumerable.Repeat("a€", 100_000));
        var decoder = new Utf8StreamDecoder();

        var pieces = decoder.DecodePieces(System.Text.Encoding.UTF8.GetBytes(replay));

        await Assert.That(string.Concat(pieces)).IsEqualTo(replay);
        await Assert.That(pieces.Count).IsGreaterThan(1);
        await Assert.That(pieces.Max(p => p.Length) * sizeof(char)).IsLessThan(85_000);
    }

    [Test]
    public async Task Flush_emits_a_replacement_for_a_dangling_partial() {
        var decoder = new Utf8StreamDecoder();
        decoder.Decode(System.Text.Encoding.UTF8.GetBytes("€").AsSpan(0, 1));
        await Assert.That(decoder.Flush()).IsEqualTo("�");
    }
}

using Capacitor.Cli.Core.Curation;

namespace Capacitor.Cli.Tests.Unit;

public class CuratedBlockTests {
    static CuratedGuideline G(string cat, string text) => new(cat, text);

    [Test]
    public async Task Render_sorts_deterministically_and_returns_null_when_empty() {
        await Assert.That(CuratedBlock.Render([])).IsNull();

        var block = CuratedBlock.Render([G("quality", "b"), G("quality", "a"), G("efficiency", "c")])!;
        // efficiency sorts before quality; within quality, "a" before "b"
        var bullets = block.Split('\n').Where(l => l.StartsWith("- ")).ToArray();
        await Assert.That(bullets).IsEquivalentTo(new[] { "- c", "- a", "- b" });
        await Assert.That(block.StartsWith(CuratedBlock.StartMarker)).IsTrue();
        await Assert.That(block.TrimEnd().EndsWith(CuratedBlock.EndMarker)).IsTrue();
    }

    [Test]
    public async Task Splice_appends_when_absent_preserving_existing() {
        var content = "# My Project\n\nHand-written notes.\n";
        var block   = CuratedBlock.Render([G("quality", "always close writers")])!;
        var result  = CuratedBlock.Splice(content, block);

        await Assert.That(result.StartsWith("# My Project")).IsTrue();
        await Assert.That(result.Contains("Hand-written notes.")).IsTrue();
        await Assert.That(result.Contains(CuratedBlock.StartMarker)).IsTrue();
        // idempotent
        await Assert.That(CuratedBlock.Splice(result, block)).IsEqualTo(result);
    }

    [Test]
    public async Task Splice_replaces_in_place_and_leaves_surrounding_untouched() {
        var head  = "# Title\n\nbefore\n";
        var tail  = "\nafter\n";
        var old   = CuratedBlock.Render([G("quality", "old line")])!;
        var start = head + old + tail;

        var fresh  = CuratedBlock.Render([G("quality", "new line")])!;
        var result = CuratedBlock.Splice(start, fresh);

        await Assert.That(result.Contains("new line")).IsTrue();
        await Assert.That(result.Contains("old line")).IsFalse();
        await Assert.That(result.Contains("before")).IsTrue();
        await Assert.That(result.Contains("after")).IsTrue();
    }

    [Test]
    public async Task Splice_removes_block_when_null() {
        var head   = "# Title\n\nkeep me\n";
        var block  = CuratedBlock.Render([G("quality", "x")])!;
        var withIt = CuratedBlock.Splice(head, block);

        var removed = CuratedBlock.Splice(withIt, null);
        await Assert.That(removed.Contains(CuratedBlock.StartMarker)).IsFalse();
        await Assert.That(removed.Contains("keep me")).IsTrue();
    }

    [Test]
    public async Task Splice_fails_closed_on_malformed_markers() {
        var startOnly = "before\n" + CuratedBlock.StartMarker + "\n- x\n";
        await Assert.That(() => CuratedBlock.Splice(startOnly, null)).Throws<CuratedBlockException>();

        var twoBlocks =
            CuratedBlock.StartMarker + "\n- a\n" + CuratedBlock.EndMarker + "\n" +
            CuratedBlock.StartMarker + "\n- b\n" + CuratedBlock.EndMarker + "\n";
        await Assert.That(() => CuratedBlock.Splice(twoBlocks, null)).Throws<CuratedBlockException>();
    }

    [Test]
    public async Task ExtractBullets_returns_block_lines() {
        var block = CuratedBlock.Render([G("quality", "a"), G("quality", "b")])!;
        var bullets = CuratedBlock.ExtractBullets("noise\n" + block + "\nmore noise\n");
        await Assert.That(bullets).IsEquivalentTo(new[] { "a", "b" });
    }
}

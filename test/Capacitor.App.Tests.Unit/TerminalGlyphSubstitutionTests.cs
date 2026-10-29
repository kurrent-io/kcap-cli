using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class TerminalGlyphSubstitutionTests {
    /// Pins Claude Code's status line with every mark swapped one-for-one, so the columns after
    /// each mark stay where the agent laid them out.
    [Test]
    public async Task Claude_Code_marks_become_one_cell_characters_the_monospace_stack_carries() {
        const string line = "⏺ Read ⎿ ok ❯ ✢✳✻✽ ⏵⏵ bypass permissions on (shift+tab to cycle)";

        var swapped = TerminalGlyphSubstitution.Apply(line);

        await Assert.That(swapped).IsEqualTo("● Read └ ok › +*** ►► bypass permissions on (shift+tab to cycle)");
        await Assert.That(swapped.Length).IsEqualTo(line.Length);
    }

    [Test]
    public async Task Text_without_a_mark_comes_back_as_the_same_instance() {
        const string line = "plain ✶ text";

        await Assert.That(TerminalGlyphSubstitution.Apply(line)).IsSameReferenceAs(line);
    }

    [Test]
    public async Task A_slice_comes_back_rewritten_and_alone() {
        await Assert.That(TerminalGlyphSubstitution.Apply("x⏺ ok⎿y", 1, 5)).IsEqualTo("● ok└");
        await Assert.That(TerminalGlyphSubstitution.Apply("x plain y", 1, 7)).IsEqualTo(" plain ");
    }
}

using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class TerminalGlyphSubstitutionTests {
    /// Pins the substitution and the width it has to preserve: the marker Claude Code's mode line
    /// opens with is replaced, and by a single-cell character, so the columns the agent laid out
    /// still line up.
    [Test]
    public async Task The_mode_marker_becomes_a_character_the_pane_can_draw() {
        var line = TerminalGlyphSubstitution.Apply("⏵⏵ auto mode on");

        await Assert.That(line).IsEqualTo("▶▶ auto mode on");
    }

    /// Pins the cheap path: a frame with nothing to swap comes back as the very same string, so
    /// the common case allocates nothing.
    [Test]
    public async Task A_frame_with_nothing_to_swap_is_returned_as_it_came() {
        const string frame = "── done ❯ ";

        await Assert.That(TerminalGlyphSubstitution.Apply(frame)).IsSameReferenceAs(frame);
    }
}

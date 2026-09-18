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
}

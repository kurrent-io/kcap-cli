namespace Capacitor.App.Services;

/// Swaps the symbols an agent's status line draws that the pane's monospace stack carries no
/// glyph for. A styled run that needs a fallback typeface is laid out on THAT typeface's
/// baseline, so the whole run — the marker and every word beside it — is drawn a few pixels below
/// the rest of the pane's rows. Each replacement is the nearest character the stack does cover,
/// and occupies one cell like the one it stands in for: a wide replacement would push every
/// column after it out of step with the layout the agent computed.
public static class TerminalGlyphSubstitution {
    // Claude Code's mode marker. Menlo and Consolas carry neither the media controls at U+23F5
    // nor anything else in that block, while the geometric shapes at U+25Bx are in both.
    const char MediaPlay = '⏵';
    const char BlackRightTriangle = '▶';

    public static string Apply(string text) => text.Replace(MediaPlay, BlackRightTriangle);
}

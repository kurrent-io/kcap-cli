namespace Capacitor.App.ViewModels;

/// One block of an expanded tool row. When IsMissing, Text says what was not recorded rather than
/// standing in for it. Note marks recorded content that is incomplete: cut or redacted.
public sealed record ToolInspectionSection(string Heading, string Text, bool IsMissing = false, string? Note = null) {
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

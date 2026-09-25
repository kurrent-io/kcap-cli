namespace Capacitor.App.ViewModels;

/// A document the plan was declared from. `Path` is the declaring machine's, relative to its
/// repository root, so the file name is cut on either separator rather than this platform's.
public sealed record PlanDocumentRow(string Kind, string Path) {
    public string FileName { get; } = Path[(Path.LastIndexOfAny(['/', '\\']) + 1)..];
}

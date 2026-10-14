using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A document the plan was declared from. `Path` is the declaring machine's, relative to its
/// repository root, so the file name is cut on either separator rather than this platform's.
public sealed class PlanDocumentRow(string kind, string path) : ReactiveObject {
    public string Kind { get; } = kind;
    public string Path { get; } = path;
    public string FileName { get; } = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    bool _isOpen;
    /// True while the Artefacts tab shows this document; the row wears the location mark.
    public bool IsOpen { get => _isOpen; internal set => this.RaiseAndSetIfChanged(ref _isOpen, value); }

    public bool Same(PlanDocumentRow other) => Kind == other.Kind && Path == other.Path;
}

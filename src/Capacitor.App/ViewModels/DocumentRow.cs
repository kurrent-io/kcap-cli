using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One document in the Artefacts tab. `Path` is the declaring machine's, relative to its repository
/// root, so the file name is cut on either separator and a path is matched by its tail.
public sealed class DocumentRow(string kind, string title, string path, string source, string contentState, string? content,
        string contentHash, long? originalBytes, DateTimeOffset discoveredAt, bool isPrimary) : ReactiveObject {
    public string Kind { get; } = kind;
    public string Title { get; } = title;
    public string Path { get; } = path;
    public string FileName { get; } = ToolCards.FileName(path);
    public string Source { get; } = source;
    public string ContentState { get; } = contentState;
    public string? Content { get; } = content;
    public string ContentHash { get; } = contentHash;
    public long? OriginalBytes { get; } = originalBytes;
    public DateTimeOffset DiscoveredAt { get; } = discoveredAt;
    public bool IsPrimary { get; } = isPrimary;

    /// "truncated" or "unavailable" beside the name; nothing for a whole body.
    public string StateChip => ContentState == "ok" ? "" : ContentState;
    public bool HasStateChip => ContentState != "ok";

    bool _isSelected;
    public bool IsSelected { get => _isSelected; internal set => this.RaiseAndSetIfChanged(ref _isSelected, value); }

    public static DocumentRow From(PlanArtifactDto dto) => new(
        dto.Kind, dto.Title, dto.Path ?? dto.Title, dto.Source, dto.ContentState, dto.Content,
        dto.ContentHash, dto.OriginalBytes, dto.DiscoveredAt, dto.IsPrimary);

    /// True when the other path ends with this row's path, on a segment boundary, whichever
    /// separator either side uses.
    public bool MatchesPath(string other) => DocumentPaths.Match(Path, other);
}

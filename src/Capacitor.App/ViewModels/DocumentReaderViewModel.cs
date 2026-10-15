using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// What the reader shows for one document: a projection built once per selection.
public sealed class DocumentReaderViewModel : ReactiveObject {
    const long Kilobyte = 1024;

    public required string KindLabel { get; init; }
    public required string FileName { get; init; }
    public required string Path { get; init; }
    public required string Body { get; init; }
    public bool HasBody => Body.Length > 0;
    string? _notice;
    public string? Notice { get => _notice; init => _notice = value; }
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public required string SizeLabel { get; init; }
    public required string DeclaredLabel { get; init; }
    public string MetaLabel => SizeLabel.Length > 0 ? $"{DeclaredLabel} · {SizeLabel}" : DeclaredLabel;

    public static DocumentReaderViewModel For(DocumentRow row, DriftState drift, DateTimeOffset now) => new() {
        KindLabel = row.KindLabel,
        FileName = row.FileName,
        Path = row.Path,
        Body = row.Content ?? "",
        Notice = NoticeFor(row, drift),
        SizeLabel = row.OriginalBytes is { } bytes ? $"{Math.Max(1, bytes / Kilobyte)} KB" : "",
        DeclaredLabel = $"{(row.Source == "declared" ? "Declared" : "Written")} {RelativeTime.Format(row.DiscoveredAt, now)}",
    };

    internal void UpdateDrift(DocumentRow row, DriftState drift) {
        var notice = NoticeFor(row, drift);
        if (Notice == notice) return;
        this.RaiseAndSetIfChanged(ref _notice, notice, nameof(Notice));
        this.RaisePropertyChanged(nameof(HasNotice));
    }

    /// A body problem outranks drift: a reader who cannot see the text has no use for a comparison.
    static string? NoticeFor(DocumentRow row, DriftState drift) => row.ContentState switch {
        "truncated"   => "Truncated: the server keeps the first 256 KB of a document.",
        "unavailable" => "No body is available: this document was declared by hash alone.",
        _ => drift switch {
            DriftState.Changed => "Working copy has changed since this was declared.",
            DriftState.Missing => "Working copy is gone: nothing is at this path any more.",
            _ => null,
        },
    };
}

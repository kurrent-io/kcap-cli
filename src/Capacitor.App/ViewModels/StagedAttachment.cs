namespace Capacitor.App.ViewModels;

/// Identity is the Id, minted at staging: two pastes of the same bytes are two chips, and a send
/// clears the chips it sent, not every chip that looks like them. Receipts hold ids, never this
/// object, so the bytes live only in a tray or a retained launch draft.
public sealed class StagedAttachment(string fileName, string contentType, ReadOnlyMemory<byte> bytes) {
    public Guid Id { get; } = Guid.NewGuid();
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public ReadOnlyMemory<byte> Bytes { get; } = bytes;
    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public string SizeLabel => Bytes.Length switch {
        < 1024 => $"{Bytes.Length} B",
        < 1024 * 1024 => $"{Bytes.Length / 1024} KB",
        _ => $"{Bytes.Length / (1024.0 * 1024.0):0.#} MB",
    };

    internal StagedAttachment Renamed(string fileName) => new(fileName, ContentType, Bytes, Id);

    StagedAttachment(string fileName, string contentType, ReadOnlyMemory<byte> bytes, Guid id) : this(fileName, contentType, bytes) => Id = id;
}

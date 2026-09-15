using System.Collections.ObjectModel;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The chips staged in one prompt. The single boundary every source passes through, so both limits
/// hold whatever produced the file.
public sealed class AttachmentTray : ReactiveObject {
    /// The wording a refusal past the per-prompt cap carries. Public because the composer groups
    /// its notice by it rather than re-deriving the sentence.
    public static readonly string CapReason = $"only {InputWire.MaxAttachmentsPerPrompt} files per message";

    /// The wording a refusal past the per-file byte cap carries, wherever the cap is enforced.
    public static readonly string SizeReason = $"is over {InputWire.MaxAttachmentBytes / (1024 * 1024)} MB";

    readonly ObservableCollection<StagedAttachment> _items = new();
    int _generation;

    public AttachmentTray() => Items = new ReadOnlyObservableCollection<StagedAttachment>(_items);

    public ReadOnlyObservableCollection<StagedAttachment> Items { get; }
    public int Count => _items.Count;
    public bool HasAttachments => _items.Count > 0;
    public long TotalBytes => _items.Sum(f => (long)f.Bytes.Length);
    public int Generation => _generation;

    public IReadOnlyList<IntakeRefusal> AddAll(IReadOnlyList<StagedAttachment> files) {
        var refused = new List<IntakeRefusal>();
        var changed = false;
        foreach (var file in files) {
            if (file.Bytes.Length > InputWire.MaxAttachmentBytes) { refused.Add(new(file.FileName, SizeReason)); continue; }
            if (_items.Count >= InputWire.MaxAttachmentsPerPrompt) { refused.Add(new(file.FileName, CapReason)); continue; }
            _items.Add(Dedup(file));
            changed = true;
        }
        if (changed) Bump();
        return refused;
    }

    public void Remove(StagedAttachment file) { if (_items.Remove(file)) Bump(); }

    public IReadOnlyList<StagedAttachment> Snapshot() => [.. _items];

    public void RemoveAll(IReadOnlyList<Guid> ids) {
        var set = ids.ToHashSet();
        var removed = false;
        for (var i = _items.Count - 1; i >= 0; i--)
            if (set.Contains(_items[i].Id)) { _items.RemoveAt(i); removed = true; }
        if (removed) Bump();
    }

    public void Restore(IReadOnlyList<StagedAttachment> snapshot) {
        _items.Clear();
        foreach (var f in snapshot) _items.Add(f);
        Bump();
    }

    public void Clear() { if (_items.Count == 0) return; _items.Clear(); Bump(); }

    StagedAttachment Dedup(StagedAttachment file) {
        if (_items.All(f => !string.Equals(f.FileName, file.FileName, StringComparison.Ordinal))) return file;
        var stem = Path.GetFileNameWithoutExtension(file.FileName);
        var ext = Path.GetExtension(file.FileName);
        for (var n = 2; ; n++) {
            var candidate = $"{stem} ({n}){ext}";
            if (_items.All(f => !string.Equals(f.FileName, candidate, StringComparison.Ordinal))) return file.Renamed(candidate);
        }
    }

    void Bump() {
        _generation++;
        this.RaisePropertyChanged(nameof(Count));
        this.RaisePropertyChanged(nameof(HasAttachments));
        this.RaisePropertyChanged(nameof(TotalBytes));
        this.RaisePropertyChanged(nameof(Generation));
    }
}

using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// An outgoing message stays here until the transcript acknowledges it. Transport acceptance
/// alone does not mean the runtime has started consuming a prompt queued behind its current turn.
public sealed class QueuedChatMessage(string text, int composerEdits, int generation, long? offset) : ReactiveObject {
    public string Text { get; } = text;
    internal int ComposerEdits { get; } = composerEdits;
    internal bool Acknowledged { get; set; }
    int _generation = generation;
    long? _offset = offset;
    internal bool HasBaseline => _offset.HasValue;

    bool _isUnconfirmed;
    public bool IsUnconfirmed { get => _isUnconfirmed; private set => this.RaiseAndSetIfChanged(ref _isUnconfirmed, value); }

    internal void MarkUnconfirmed() => IsUnconfirmed = true;

    /// A new transcript may contain either replayed history or the lost echo. Neither is safe
    /// evidence. Retain the message, then allow only subsequent appends in this generation.
    internal void Rebase(int generation, long? offset) {
        _generation = generation;
        _offset = offset;
        MarkUnconfirmed();
    }

    internal bool Matches(string text, int generation, long offset) =>
        _generation == generation && _offset is { } baseline && offset >= baseline
        && Normalize(Text) == Normalize(text);

    static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}

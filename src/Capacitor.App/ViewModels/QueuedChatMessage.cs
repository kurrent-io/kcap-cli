using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// An outgoing message stays here until the transcript acknowledges it. Transport acceptance
/// alone does not mean the runtime has started consuming a prompt queued behind its current turn.
/// Only the ids are held: the chips themselves live in the tray until a delivery clears them.
public sealed class QueuedChatMessage(string text, int composerEdits, int generation, long? offset, IReadOnlyList<Guid> attachmentIds) : ReactiveObject {
    public string Text { get; } = text;
    public IReadOnlyList<Guid> AttachmentIds { get; } = attachmentIds;
    internal int ComposerEdits { get; } = composerEdits;
    internal bool Acknowledged { get; set; }
    int _generation = generation;
    long? _offset = offset;
    internal bool HasBaseline => _offset.HasValue;

    bool _isUnconfirmed;
    public bool IsUnconfirmed { get => _isUnconfirmed; private set => this.RaiseAndSetIfChanged(ref _isUnconfirmed, value); }

    internal void MarkUnconfirmed() => IsUnconfirmed = true;

    /// The server's id for this prompt once it has listed it; null until then.
    internal Guid? DispatchId { get; private set; }
    /// Queued by another client: shown, never acknowledged here, retired when the server drops it.
    public bool IsForeign { get; private init; }
    public string Sender { get; private init; } = "";

    internal static QueuedChatMessage FromServer(QueuedInputItem item) =>
        new(item.Text, composerEdits: -1, generation: -1, offset: null, attachmentIds: []) { DispatchId = item.DispatchId, IsForeign = true, Sender = item.SenderUserId ?? "" };

    internal void MarkQueued(Guid dispatchId) {
        DispatchId = dispatchId;
        IsUnconfirmed = false;
    }

    internal bool MatchesText(string text) => Normalize(Text) == Normalize(text);

    /// A new transcript may contain either replayed history or the lost echo. Neither is safe
    /// evidence. Retain the message, then allow only subsequent appends in this generation.
    internal void Rebase(int generation, long? offset) {
        _generation = generation;
        _offset = offset;
        MarkUnconfirmed();
    }

    /// The daemon appends the trailer to a prompt that carried files, so that turn — and not the
    /// bare text, which may be replayed history — is the only receipt an attachment send has. The
    /// trailer follows the prompt untrimmed, so whatever trailing whitespace Normalize took off the
    /// sent text still stands between the two in the echo.
    internal bool Matches(string text, int generation, long offset) {
        if (_generation != generation || _offset is not { } baseline || offset < baseline) return false;
        var sent = Normalize(Text);
        var seen = Normalize(text);
        if (AttachmentIds.Count == 0) return sent == seen;
        return seen.Length > sent.Length
            && seen.StartsWith(sent, StringComparison.Ordinal)
            && seen.AsSpan(sent.Length).TrimStart().StartsWith(AttachmentTrailer.Prefix, StringComparison.Ordinal);
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}

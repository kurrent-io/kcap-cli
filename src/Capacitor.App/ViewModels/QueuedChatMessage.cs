using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// An outgoing message stays here until the transcript acknowledges it. Transport acceptance
/// alone does not mean the runtime has started consuming a prompt queued behind its current turn.
/// Only the ids are held: the chips themselves live in the tray until a delivery clears them.
public sealed class QueuedChatMessage(string text, int composerEdits, int generation, long? offset, IReadOnlyList<Guid> attachmentIds) : ReactiveObject {
    public const string AwaitingPickupNote = "The agent reads it when its current step ends";
    /// Shown instead of <see cref="AwaitingPickupNote"/> when nothing is in a step to end.
    public const string IdlePickupNote = "The agent is not in a step";

    public string Text { get; } = text;
    public IReadOnlyList<Guid> AttachmentIds { get; } = attachmentIds;
    internal int ComposerEdits { get; } = composerEdits;
    internal bool Acknowledged { get; set; }
    int _generation = generation;
    long? _offset = offset;
    internal bool HasBaseline => _offset.HasValue;

    bool _isUnconfirmed;
    public bool IsUnconfirmed {
        get => _isUnconfirmed;
        private set {
            if (_isUnconfirmed == value) return;
            _isUnconfirmed = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsAwaitingPickup));
            this.RaisePropertyChanged(nameof(PickupNote));
        }
    }

    bool _agentIdle;
    bool _canSendNow;
    /// The agent is idle and the channel can take text, so this row can be delivered instead of waiting.
    public bool CanSendNow {
        get => _canSendNow;
        private set {
            if (_canSendNow == value) return;
            _canSendNow = value;
            this.RaisePropertyChanged();
        }
    }

    /// Which wait the row describes. A channel that is still delivering the last paste is not a step.
    public string? PickupNote => !IsAwaitingPickup ? null : _agentIdle ? IdlePickupNote : AwaitingPickupNote;

    internal void SetPickup(bool agentIdle, bool canSend) {
        // A server-listed prompt is already a live dispatch: offering Send would submit another.
        var send = canSend && IsAwaitingPickup && AttachmentIds.Count == 0 && DispatchId is null;
        var idleChanged = _agentIdle != agentIdle;
        var sendChanged = _canSendNow != send;
        _agentIdle = agentIdle;
        if (sendChanged) CanSendNow = send;
        if (idleChanged) this.RaisePropertyChanged(nameof(PickupNote));
    }

    /// An own send the channel accepted but the transcript has not echoed: the runtime reads a
    /// prompt queued behind its current turn only when that turn's step ends. A dispatch the server
    /// has stopped listing was delivered or withdrawn, and only the echo can say which.
    public bool IsAwaitingPickup => !IsForeign && !IsUnconfirmed && (DispatchId is null || _serverListed);

    internal void MarkUnconfirmed() => IsUnconfirmed = true;

    /// The server's id for this prompt once it has listed it; null until then.
    internal Guid? DispatchId { get; private set; }
    bool _serverListed;

    internal void MarkUnlisted() {
        if (!_serverListed) return;
        _serverListed = false;
        this.RaisePropertyChanged(nameof(IsAwaitingPickup));
        this.RaisePropertyChanged(nameof(PickupNote));
    }
    /// Queued by another client: shown, never acknowledged here, retired when the server drops it.
    public bool IsForeign { get; private init; }

    internal static QueuedChatMessage FromServer(QueuedInputItem item) =>
        new(item.Text, composerEdits: -1, generation: -1, offset: null, attachmentIds: []) { DispatchId = item.DispatchId, IsForeign = true };

    internal void MarkQueued(Guid dispatchId) {
        DispatchId = dispatchId;
        _serverListed = true;
        IsUnconfirmed = false;
        this.RaisePropertyChanged(nameof(IsAwaitingPickup));
        this.RaisePropertyChanged(nameof(PickupNote));
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
        if (AttachmentIds.Count == 0) return SamePrompt(sent, seen);
        // The receipt drops a space after the bang, and the attachment trailer follows that body.
        if (BangBody(sent) is { } sentBody && BangBody(seen) is { } seenBody)
            return HasAttachmentTail(sentBody, seenBody);
        return HasAttachmentTail(sent, seen);
    }

    static bool HasAttachmentTail(string sent, string seen) =>
        seen.Length > sent.Length
        && seen.StartsWith(sent, StringComparison.Ordinal)
        && seen.AsSpan(sent.Length).TrimStart().StartsWith(AttachmentTrailer.Prefix, StringComparison.Ordinal);

    static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();

    /// A bang command's receipt drops the space the composer may have put after the bang, and a
    /// slash command's rejoins its name and trimmed arguments with a single space.
    static bool SamePrompt(string sent, string seen) =>
        sent == seen
        || BangBody(sent) is { } a && BangBody(seen) is { } b && a == b
        || SlashPrompt(sent) is { } c && SlashPrompt(seen) is { } d && c == d;

    static string? SlashPrompt(string text) {
        if (text.Length == 0 || text[0] != '/') return null;
        var separator = text.AsSpan().IndexOfAny(' ', '\t', '\n');
        return separator < 0 ? text : $"{text[..separator]} {text[(separator + 1)..].Trim()}";
    }

    static string? BangBody(string text) {
        if (text.Length == 0 || text[0] != '!') return null;
        var body = text[1..].TrimStart();
        return body.Length == 0 ? null : body;
    }
}

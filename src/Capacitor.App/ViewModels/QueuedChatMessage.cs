using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// An outgoing message stays here until the transcript acknowledges it. Transport acceptance
/// alone does not mean the runtime has started consuming a prompt queued behind its current turn.
/// Only the ids are held: the chips themselves live in the tray until a delivery clears them.
public sealed class QueuedChatMessage(string text, int composerEdits, int generation, long? offset, IReadOnlyList<Guid> attachmentIds) : ReactiveObject {
    public const string AwaitingPickupNote = "The agent reads it when its current step ends";
    public const string AttachmentsOnlyText = "Attachments only";
    public const string IdlePickupNote = "The agent is not in a step";
    public const string UnknownPickupNote = "Waiting for delivery confirmation from the agent";

    public string Text { get; } = text;
    /// The server's queue item carries no attachments, so a prompt that was only files arrives
    /// with empty text.
    public string DisplayText => string.IsNullOrWhiteSpace(Text) ? AttachmentsOnlyText : Text;
    public IReadOnlyList<Guid> AttachmentIds { get; } = attachmentIds;
    internal int ComposerEdits { get; } = composerEdits;
    internal bool Acknowledged { get; set; }
    /// Own sends are numbered in the order they were handed to the channel. The file offset
    /// stays put until the transcript grows, so two sends can share a baseline.
    internal int SendOrder { get; set; }
    internal bool SentIdle { get; set; }
    internal bool IsSlashCommand =>
        AttachmentIds.Count == 0 && Normalize(Text) is ['/', ..] command && !command.Contains('\n');
    internal ViewerIdentityDto? SenderIdentity { get; init; }
    int _generation = generation;
    long? _offset = offset;
    internal bool HasBaseline => _offset.HasValue;
    internal long? Baseline => _offset;

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

    bool? _agentIdle;
    bool _canSendNow;
    bool _canDiscard;
    public bool CanDiscard {
        get => _canDiscard;
        private set {
            if (_canDiscard == value) return;
            _canDiscard = value;
            this.RaisePropertyChanged();
        }
    }
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
    public string? PickupNote => !IsAwaitingPickup ? null : _agentIdle switch {
        true => IdlePickupNote,
        false => AwaitingPickupNote,
        null => UnknownPickupNote,
    };

    /// <param name="canDiscard">Whether a row no server holds can be dropped from the strip.</param>
    /// <param name="canDiscardHeld">Whether the channel can ask the server to drop a row it holds.</param>
    internal void SetPickup(bool? agentIdle, bool canSend, bool canDiscard, bool canDiscardHeld) {
        var idleChanged = _agentIdle != agentIdle;
        _agentIdle = agentIdle;
        // The server retires the oldest pending dispatch an echo matches, so a second copy of a
        // held prompt would be the one left pending. A held row can only be discarded.
        CanSendNow = canSend && !IsForeign && !Acknowledged && AttachmentIds.Count == 0 && !IsServerHeld;
        CanDiscard = !Acknowledged && (IsServerHeld ? canDiscardHeld : canDiscard);
        if (idleChanged) this.RaisePropertyChanged(nameof(PickupNote));
    }

    /// An own send the channel accepted but the transcript has not echoed: the runtime reads a
    /// prompt queued behind its current turn only when that turn's step ends.
    public bool IsAwaitingPickup => !IsForeign && !IsUnconfirmed;

    /// In the server's queue now, so only the server can retire it.
    internal bool IsServerHeld => IsForeign || DispatchId is not null && _serverListed;

    internal void MarkUnconfirmed() => IsUnconfirmed = true;

    /// The server's id for this prompt once it has listed it; null until then.
    internal Guid? DispatchId { get; private set; }
    bool _serverListed;

    /// The server dropped it because the agent echoed it, the daemon refused it, someone discarded
    /// it or the run ended. Only an echo here can tell delivery from the rest.
    internal void MarkUnlisted() {
        if (!_serverListed) return;
        _serverListed = false;
        MarkUnconfirmed();
    }
    /// A server entry unmatched to this pane's sends. Its originating client is unknown.
    public bool IsForeign { get; private init; }

    internal static QueuedChatMessage FromServer(QueuedInputItem item) =>
        new(item.Text, composerEdits: -1, generation: -1, offset: null, attachmentIds: []) { DispatchId = item.DispatchId, IsForeign = true };

    internal void MarkQueued(Guid dispatchId) {
        if (DispatchId == dispatchId && _serverListed && !IsUnconfirmed) return;
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
        SentIdle = false;
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

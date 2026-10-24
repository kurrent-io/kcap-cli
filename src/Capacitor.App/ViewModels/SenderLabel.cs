using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The title over a user message once the server names its sender. Until then the chat shows its own
/// fallback, which follows the session's ownership.
public sealed class SenderLabel(MessageKeys keys, DateTimeOffset? sentAt) : ReactiveObject {
    internal MessageKeys Keys { get; } = keys;
    internal DateTimeOffset? SentAt { get; } = sentAt;

    string? _title;
    public string? Title {
        get => _title;
        internal set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public bool HasTitle => _title is not null;
}

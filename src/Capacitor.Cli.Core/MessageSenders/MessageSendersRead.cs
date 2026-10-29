namespace Capacitor.Cli.Core.MessageSenders;

public sealed record MessageSendersRead(MessageSendersReadKind Kind, IReadOnlyList<MessageSenderDto> Senders) {
    public static MessageSendersRead Of(MessageSendersReadKind kind) => new(kind, []);
}

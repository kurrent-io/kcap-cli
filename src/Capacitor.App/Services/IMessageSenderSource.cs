using Capacitor.Cli.Core.MessageSenders;

namespace Capacitor.App.Services;

/// Who sent a session's user messages, however the app reaches the server.
public interface IMessageSenderSource {
    Task<MessageSendersRead> ReadAsync(string sessionId, MessageSendersRequest request, CancellationToken ct);
}

using Capacitor.App.Services;
using Capacitor.Cli.Core.MessageSenders;

namespace Capacitor.App.Tests.Unit;

/// Scripted IMessageSenderSource: every read records what it was asked and answers from <see cref="Answer"/>.
sealed class FakeMessageSenderSource : IMessageSenderSource {
    public readonly List<(string SessionId, MessageSendersRequest Request)> Reads = [];
    public Func<MessageSendersRequest, MessageSendersRead> Answer = _ => new(MessageSendersReadKind.Ready, []);

    public async Task<MessageSendersRead> ReadAsync(string sessionId, MessageSendersRequest request, CancellationToken ct) {
        Reads.Add((sessionId, request));
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        return Answer(request);
    }
}

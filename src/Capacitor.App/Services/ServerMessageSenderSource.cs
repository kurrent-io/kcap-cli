using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.MessageSenders;

namespace Capacitor.App.Services;

public sealed class ServerMessageSenderSource : IMessageSenderSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<MessageSendersClient> _reads;

    public ServerMessageSenderSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<MessageSendersClient>.ClientFactory? factory = null,
            LocalRoot? localRoot = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new MessageSendersClient(http, url), factory, localRoot: localRoot);
    }

    public Task<MessageSendersRead> ReadAsync(string sessionId, MessageSendersRequest request, CancellationToken ct) =>
        _reads.ReadAsync(
            (channel, token) => channel.ReadAsync(sessionId, request, token), read => read.Kind == MessageSendersReadKind.SignedOut,
            MessageSendersRead.Of(MessageSendersReadKind.SignedOut), MessageSendersRead.Of(MessageSendersReadKind.Unreachable), ct);

    public void InvalidateAuthentication() => _reads.Invalidate();

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
}

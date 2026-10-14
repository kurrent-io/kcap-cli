using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.NextWork;

namespace Capacitor.App.Services;

public sealed class ServerNextWorkSource : INextWorkSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<NextWorkClient> _reads;

    public ServerNextWorkSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<NextWorkClient>.ClientFactory? factory = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new NextWorkClient(http, url), factory);
    }

    public Task<NextWorkRead> ReadAsync(CancellationToken ct) => _reads.ReadAsync(
        (channel, token) => channel.ReadAsync(NextWorkClient.DefaultLimit, token),
        read => read.Kind == NextWorkReadKind.SignedOut,
        NextWorkRead.Of(NextWorkReadKind.SignedOut),
        NextWorkRead.Of(NextWorkReadKind.Unreachable),
        ct);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() => _reads.Invalidate();
}

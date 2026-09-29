using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

public sealed class ServerPlanSource : IPlanSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<SessionPlansClient> _reads;

    public ServerPlanSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<SessionPlansClient>.ClientFactory? factory = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new SessionPlansClient(http, url), factory);
    }

    public Task<SessionPlansRead> ReadAsync(string sessionId, CancellationToken ct) => _reads.ReadAsync(
        (channel, token) => channel.ReadAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
        SessionPlansRead.Of(SessionPlansReadKind.SignedOut), SessionPlansRead.Of(SessionPlansReadKind.Unreachable), ct);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() => _reads.Invalidate();
}

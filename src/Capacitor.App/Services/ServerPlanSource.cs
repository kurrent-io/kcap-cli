using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services;

public sealed class ServerPlanSource : IPlanSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<SessionPlansClient> _reads;
    readonly LastReads<SessionPlansRead> _last = new();

    public ServerPlanSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<SessionPlansClient>.ClientFactory? factory = null,
            LocalRoot? localRoot = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new SessionPlansClient(http, url), factory, localRoot: localRoot);
    }

    public async Task<SessionPlansRead> ReadAsync(string sessionId, CancellationToken ct) {
        var read = await _last.RecordAsync(sessionId, () => _reads.ReadAsync(
            (channel, token) => channel.ReadAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
            SessionPlansRead.Of(SessionPlansReadKind.SignedOut), SessionPlansRead.Of(SessionPlansReadKind.Unreachable), ct),
            read => read.Kind switch { SessionPlansReadKind.Ready => true, SessionPlansReadKind.Unreachable => null, _ => false }).ConfigureAwait(false);
        // A lapsed credential ends what every session's cache may still show.
        if (read.Kind == SessionPlansReadKind.SignedOut) _last.Clear();
        return read;
    }

    public SessionPlansRead? Cached(string sessionId) => _last.Get(sessionId);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() {
        _reads.Invalidate();
        _last.Clear();
    }
}

using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

public sealed class ServerPageSource : IPageSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<PagesClient> _reads;
    readonly LastReads<SessionPagesRead> _last = new(capacity: 32);

    public ServerPageSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<PagesClient>.ClientFactory? factory = null,
            LocalRoot? localRoot = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new PagesClient(http, url), factory, localRoot: localRoot);
    }

    public async Task<SessionPagesRead> ReadAsync(string sessionId, CancellationToken ct) {
        var read = await _last.RecordAsync(sessionId, () => _reads.ReadAsync(
            (channel, token) => channel.ListForSessionAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
            SessionPagesRead.Of(SessionPlansReadKind.SignedOut), SessionPagesRead.Of(SessionPlansReadKind.Unreachable), ct),
            read => read.Kind switch { SessionPlansReadKind.Ready => true, SessionPlansReadKind.Unreachable => null, _ => false }).ConfigureAwait(false);
        // A lapsed credential ends what every session's cache may still show.
        if (read.Kind == SessionPlansReadKind.SignedOut) _last.Clear();
        return read;
    }

    public SessionPagesRead? Cached(string sessionId) => _last.Get(sessionId);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() {
        _reads.Invalidate();
        _last.Clear();
    }
}

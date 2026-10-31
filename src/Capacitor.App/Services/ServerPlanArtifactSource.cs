using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services;

public sealed class ServerPlanArtifactSource : IPlanArtifactSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<ArtefactReadChannel> _reads;
    // Fewer than the other sources keep: these reads carry the document bodies.
    readonly LastReads<PlanArtifactsRead> _last = new(capacity: 16);
    readonly LastReads<SessionPagesRead> _lastPages = new();

    public ServerPlanArtifactSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<ArtefactReadChannel>.ClientFactory? factory = null,
            LocalRoot? localRoot = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new ArtefactReadChannel(http, url), factory, localRoot: localRoot);
    }

    public async Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) {
        var read = await _last.RecordAsync(sessionId, () => _reads.ReadAsync(
            (channel, token) => channel.Plans.ReadAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
            PlanArtifactsRead.Of(SessionPlansReadKind.SignedOut), PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable), ct),
            read => Remember(read.Kind)).ConfigureAwait(false);
        if (read.Kind == SessionPlansReadKind.SignedOut) ClearCaches();
        return read;
    }

    public PlanArtifactsRead? Cached(string sessionId) => _last.Get(sessionId);

    public async Task<SessionPagesRead> ReadPagesAsync(string sessionId, CancellationToken ct) {
        var read = await _lastPages.RecordAsync(sessionId, () => _reads.ReadAsync(
            (channel, token) => channel.Pages.ReadAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
            SessionPagesRead.Of(SessionPlansReadKind.SignedOut), SessionPagesRead.Of(SessionPlansReadKind.Unreachable), ct),
            read => Remember(read.Kind)).ConfigureAwait(false);
        if (read.Kind == SessionPlansReadKind.SignedOut) ClearCaches();
        return read;
    }

    public SessionPagesRead? CachedPages(string sessionId) => _lastPages.Get(sessionId);

    static bool? Remember(SessionPlansReadKind kind) =>
        kind switch { SessionPlansReadKind.Ready => true, SessionPlansReadKind.Unreachable => null, _ => false };

    /// A lapsed credential ends what every session's cache may still show.
    void ClearCaches() {
        _last.Clear();
        _lastPages.Clear();
    }

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() {
        _reads.Invalidate();
        ClearCaches();
    }
}

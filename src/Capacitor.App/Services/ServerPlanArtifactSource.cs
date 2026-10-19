using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services;

public sealed class ServerPlanArtifactSource : IPlanArtifactSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<PlanArtifactsClient> _reads;
    // Fewer than the other sources keep: these reads carry the document bodies.
    readonly LastReads<PlanArtifactsRead> _last = new(capacity: 16);

    public ServerPlanArtifactSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<PlanArtifactsClient>.ClientFactory? factory = null,
            LocalRoot? localRoot = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new PlanArtifactsClient(http, url), factory, localRoot: localRoot);
    }

    public async Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) {
        var read = await _last.RecordAsync(sessionId, () => _reads.ReadAsync(
            (channel, token) => channel.ReadAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
            PlanArtifactsRead.Of(SessionPlansReadKind.SignedOut), PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable), ct),
            read => read.Kind switch { SessionPlansReadKind.Ready => true, SessionPlansReadKind.Unreachable => null, _ => false }).ConfigureAwait(false);
        // A lapsed credential ends what every session's cache may still show.
        if (read.Kind == SessionPlansReadKind.SignedOut) _last.Clear();
        return read;
    }

    public PlanArtifactsRead? Cached(string sessionId) => _last.Get(sessionId);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() {
        _reads.Invalidate();
        _last.Clear();
    }
}

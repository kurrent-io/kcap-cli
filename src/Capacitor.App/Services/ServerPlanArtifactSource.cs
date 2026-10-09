using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Services;

public sealed class ServerPlanArtifactSource : IPlanArtifactSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<PlanArtifactsClient> _reads;

    public ServerPlanArtifactSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<PlanArtifactsClient>.ClientFactory? factory = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new PlanArtifactsClient(http, url), factory);
    }

    public Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) => _reads.ReadAsync(
        (channel, token) => channel.ReadAsync(sessionId, token), read => read.Kind == SessionPlansReadKind.SignedOut,
        PlanArtifactsRead.Of(SessionPlansReadKind.SignedOut), PlanArtifactsRead.Of(SessionPlansReadKind.Unreachable), ct);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() => _reads.Invalidate();
}

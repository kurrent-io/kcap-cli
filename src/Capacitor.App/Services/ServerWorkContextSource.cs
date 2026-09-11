using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Services;

public sealed class ServerWorkContextSource : IWorkContextSource, IAsyncDisposable {
    public delegate Task<(HttpClient Client, AuthStatus Status)> ClientFactory(ConfigRoot config, ProfileContext profiles, string serverUrl, CancellationToken ct);
    readonly AuthenticatedServerReads<WorkContextClient> _reads;

    public ServerWorkContextSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, ClientFactory? factory = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new WorkContextClient(http, url),
            factory is null ? null : (c, p, url, ct) => factory(c, p, url, ct));
    }
    public Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct) => _reads.ReadAsync(
        (channel, token) => WorkContextReader.ReadAsync(channel, sessionId, token), read => read.Kind == WorkContextReadKind.SignedOut,
        WorkContextRead.Of(WorkContextReadKind.SignedOut), WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed"), ct);
    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() => _reads.Invalidate();
}

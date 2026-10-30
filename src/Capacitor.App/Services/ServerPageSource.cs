using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
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

    public Task<PageContentRead> ReadContentAsync(string artefactId, int? version, CancellationToken ct) =>
        Uncached((channel, token) => channel.ReadContentAsync(artefactId, version, token), read => read is PageContentRead.SignedOut,
            new PageContentRead.SignedOut(), new PageContentRead.Unreachable(), ct);

    public Task<PageBridgeReply> SubmitAsync(string artefactId, int version, JsonElement payload, CancellationToken ct) =>
        Bridge((channel, token) => channel.SubmitAsync(artefactId, version, payload, token), ct);

    public Task<PageBridgeReply> MineAsync(string artefactId, int version, CancellationToken ct) =>
        Bridge((channel, token) => channel.MineAsync(artefactId, version, token), ct);

    public Task<PageBridgeReply> ResultsAsync(string artefactId, int version, CancellationToken ct) =>
        Bridge((channel, token) => channel.ResultsAsync(artefactId, version, token), ct);

    public Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct) =>
        _reads.ReadAsync((channel, token) => channel.ResultsAccessAsync(artefactId, page, token), _ => false,
            PageResultsAccess.Unknown, PageResultsAccess.Unknown, ct);

    Task<PageBridgeReply> Bridge(Func<PagesClient, CancellationToken, Task<PageBridgeReply>> call, CancellationToken ct) =>
        Uncached(call, reply => reply.Status == 401, new PageBridgeReply(401, null), new PageBridgeReply(0, null), ct);

    async Task<T> Uncached<T>(Func<PagesClient, CancellationToken, Task<T>> call, Func<T, bool> signedOut, T noAuth, T disposed, CancellationToken ct) {
        var result = await _reads.ReadAsync(call, signedOut, noAuth, disposed, ct).ConfigureAwait(false);
        if (signedOut(result)) _last.Clear();
        return result;
    }

    public SessionPagesRead? Cached(string sessionId) => _last.Get(sessionId);

    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() {
        _reads.Invalidate();
        _last.Clear();
    }
}

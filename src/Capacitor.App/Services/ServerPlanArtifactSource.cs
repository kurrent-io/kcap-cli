using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services;

public sealed class ServerPlanArtifactSource : IPlanArtifactSource, IPageContentSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<ArtefactReadChannel> _reads;
    // Fewer than the other sources keep: these reads carry the document bodies.
    readonly LastReads<PlanArtifactsRead> _last = new(capacity: 16);
    readonly LastReads<SessionPagesRead> _lastPages = new();
    readonly Func<CancellationToken, Task<string?>>? _viewerName;

    public ServerPlanArtifactSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<ArtefactReadChannel>.ClientFactory? factory = null,
            LocalRoot? localRoot = null, Func<CancellationToken, Task<string?>>? viewerName = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new ArtefactReadChannel(http, url), factory, localRoot: localRoot);
        _viewerName = viewerName;
    }

    /// The name the stored sign-in carries for this profile, which is the one the app shows.
    public static Func<CancellationToken, Task<string?>> StoredSignInName(TokenStore tokens, ProfileContext? profiles) =>
        async ct => profiles is null ? null : (await tokens.LoadForProfileAsync(profiles.Name, ct).ConfigureAwait(false))?.GitHubUsername;

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

    public Task<PageContentRead> ReadContentAsync(string artefactId, int? version, CancellationToken ct) =>
        Uncached((channel, token) => channel.PageContent.ReadContentAsync(artefactId, version, token), read => read is PageContentRead.SignedOut,
            new PageContentRead.SignedOut(), new PageContentRead.Unreachable(), ct);

    public Task<PageBridgeReply> SubmitAsync(string artefactId, int version, JsonElement payload, CancellationToken ct) =>
        Bridge((channel, token) => channel.PageContent.SubmitAsync(artefactId, version, payload, token), ct);

    public Task<PageBridgeReply> MineAsync(string artefactId, int version, CancellationToken ct) =>
        Bridge((channel, token) => channel.PageContent.MineAsync(artefactId, version, token), ct);

    public Task<PageBridgeReply> ResultsAsync(string artefactId, int version, CancellationToken ct) =>
        Bridge((channel, token) => channel.PageContent.ResultsAsync(artefactId, version, token), ct);

    public Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct) =>
        Uncached((channel, token) => channel.PageContent.ResultsAccessAsync(artefactId, page, token), access => access == PageResultsAccess.SignedOut,
            PageResultsAccess.SignedOut, PageResultsAccess.Unknown, ct);

    public async Task<string> ViewerNameAsync(CancellationToken ct) {
        if (_viewerName is null) return "";
        try {
            return await _viewerName(ct).ConfigureAwait(false) ?? "";
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: could not read the signed-in name: {ex.Message}");
            return "";
        }
    }

    Task<PageBridgeReply> Bridge(Func<ArtefactReadChannel, CancellationToken, Task<PageBridgeReply>> call, CancellationToken ct) =>
        Uncached(call, reply => reply.Status == 401, new PageBridgeReply(401, null), new PageBridgeReply(0, null), ct);

    async Task<T> Uncached<T>(Func<ArtefactReadChannel, CancellationToken, Task<T>> call, Func<T, bool> signedOut, T noAuth, T disposed, CancellationToken ct) {
        var result = await _reads.ReadAsync(call, signedOut, noAuth, disposed, ct).ConfigureAwait(false);
        if (signedOut(result)) ClearCaches();
        return result;
    }

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

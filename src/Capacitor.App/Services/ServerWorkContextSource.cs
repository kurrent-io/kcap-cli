using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services;

public sealed class ServerWorkContextSource : IWorkContextSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<WorkContextClient> _reads;
    readonly LastReads<WorkContextRead> _last = new();
    // _last.Generation moves on every credential change the app sees, so a viewer settled under an
    // older one, or by a read that began before the change, is never served. The lifetime covers
    // the change it cannot see: the recovery handler can adopt another account's stored token.
    public static readonly TimeSpan ViewerLifetime = TimeSpan.FromMinutes(5);
    readonly Lock _viewerLock = new();
    readonly TimeProvider _time;
    SettledViewer? _viewer;

    sealed record SettledViewer(long Generation, DateTimeOffset At, ViewerIdentityDto? Identity);

    public ServerWorkContextSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<WorkContextClient>.ClientFactory? factory = null,
            TimeProvider? time = null,
            LocalRoot? localRoot = null) {
        _time = time ?? TimeProvider.System;
        _reads = new(config, profiles, env, machine, (http, url) => new WorkContextClient(http, url), factory, localRoot: localRoot);
    }
    public async Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct) {
        var read = await _last.RecordAsync(sessionId, () => ReadWithViewerAsync(sessionId, ct),
            read => read.Kind switch { WorkContextReadKind.Ready => true, WorkContextReadKind.Unreachable => null, _ => false }).ConfigureAwait(false);
        // A lapsed credential ends what every session's cache may still show.
        if (read.Kind == WorkContextReadKind.SignedOut) _last.Clear();
        return read;
    }

    async Task<WorkContextRead> ReadWithViewerAsync(string sessionId, CancellationToken ct) {
        long generation;
        SettledViewer? known;
        lock (_viewerLock) {
            generation = _last.Generation;
            known = _viewer is { } v && v.Generation == generation && _time.GetUtcNow() - v.At < ViewerLifetime ? v : null;
        }
        var read = await _reads.ReadAsync(
            (channel, token) => WorkContextReader.ReadAsync(channel, sessionId, readViewer: known is null, token), read => read.Kind == WorkContextReadKind.SignedOut,
            WorkContextRead.Of(WorkContextReadKind.SignedOut), WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed"), ct).ConfigureAwait(false);
        if (read.Kind != WorkContextReadKind.Ready) return read;

        lock (_viewerLock) {
            if (generation != _last.Generation) return read with { Viewer = null };
            if (known is null && !read.ViewerFailed) known = _viewer = new(generation, _time.GetUtcNow(), read.Viewer);
            return read with { Viewer = known?.Identity };
        }
    }

    public WorkContextRead? Cached(string sessionId) => _last.Get(sessionId);
    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() {
        _reads.Invalidate();
        _last.Clear();
    }
}

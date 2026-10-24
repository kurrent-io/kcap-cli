using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services;

public sealed class ServerWorkContextSource : IWorkContextSource, IAsyncDisposable {
    readonly AuthenticatedServerReads<WorkContextClient> _reads;
    readonly LastReads<WorkContextRead> _last = new();
    // Asked once per credential: who the viewer is does not change between sessions.
    volatile ViewerIdentityDto? _viewer;
    volatile bool _viewerSettled;

    public ServerWorkContextSource(ConfigRoot config, ProfileContext? profiles, ProfileOverrides env,
            MachineAuth machine, AuthenticatedServerReads<WorkContextClient>.ClientFactory? factory = null,
            LocalRoot? localRoot = null) {
        _reads = new(config, profiles, env, machine, (http, url) => new WorkContextClient(http, url), factory, localRoot: localRoot);
    }
    public async Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct) {
        var readViewer = !_viewerSettled;
        var read = await _last.RecordAsync(sessionId, () => _reads.ReadAsync(
            (channel, token) => WorkContextReader.ReadAsync(channel, sessionId, readViewer, token), read => read.Kind == WorkContextReadKind.SignedOut,
            WorkContextRead.Of(WorkContextReadKind.SignedOut), WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed"), ct),
            read => read.Kind switch { WorkContextReadKind.Ready => true, WorkContextReadKind.Unreachable => null, _ => false }).ConfigureAwait(false);
        // A lapsed credential ends what every session's cache may still show.
        if (read.Kind == WorkContextReadKind.SignedOut) {
            _last.Clear();
            ForgetViewer();
            return read;
        }
        if (readViewer && read.Kind == WorkContextReadKind.Ready && !read.ViewerFailed) {
            _viewer = read.Viewer;
            _viewerSettled = true;
        }
        return read with { Viewer = _viewer };
    }
    public WorkContextRead? Cached(string sessionId) => _last.Get(sessionId);
    public ValueTask DisposeAsync() => _reads.DisposeAsync();
    public void InvalidateAuthentication() {
        _reads.Invalidate();
        _last.Clear();
        ForgetViewer();
    }

    void ForgetViewer() {
        _viewerSettled = false;
        _viewer = null;
    }
}

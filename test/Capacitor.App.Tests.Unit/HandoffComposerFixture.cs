using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Builds real hand-off composers for a session row, over a server and a daemon that both carry
/// handoff/1 and a checkout of acme/widgets the probe answers for.
sealed class HandoffComposerFixture : IDisposable {
    public const string Checkout = "/w/widgets";

    public FakeDaemonClientService Daemon { get; } = new();
    public FakeHandoffLaunchClient Launch { get; } = new();
    public FakeServerSessionsService Sessions { get; } = new();
    public FakeHandoffProbeOps Ops { get; } = new();
    public ScriptedUploader Uploader { get; } = new();
    public Subject<LaunchFailure> Failures { get; } = new();
    public BehaviorSubject<ServerLaneStatus> Lane { get; } = new(new ServerLaneStatus(ServerLaneState.Connected));
    public FakeAgentDirectory Directory { get; } = new();
    public HandoffDrafts Drafts { get; }
    public List<HandoffComposerViewModel> Built { get; } = [];
    public List<string> Reopened { get; } = [];
    public RecordingNotifier Notifier { get; } = new();
    readonly Dictionary<string, string> _origins = new() { [Checkout] = "https://github.com/acme/widgets.git" };

    public HandoffComposerFixture() {
        Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));
        Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", "input/2", HandoffWire.Capability]));
        Drafts = new HandoffDrafts(Failures, Directory, new FakeTimeProvider());
    }

    public HandoffComposerViewModel Build(AgentRow row) {
        var vm = new HandoffComposerViewModel(
            HandoffComposerViewModel.SourceOf(row), Daemon, Launch, Sessions, Ops,
            new LocalCheckoutFinder(new RepoIdentityResolver(root => _origins.GetValueOrDefault(root)), mainRoot: p => p),
            _ => Task.FromResult<IReadOnlyList<string>>([Checkout]),
            Drafts, Lane, Uploader, Directory,
            () => 0, (_, _, _) => { }, _ => { },
            (sessionId, _) => { Reopened.Add(sessionId); return Task.FromResult(true); }, Notifier);
        Sessions.HandoffContext = new HandoffContextDto(row.SessionId ?? row.Id, "acme", "widgets", "feature/x", null, ["c1"]);
        Built.Add(vm);
        return vm;
    }

    public void Dispose() {
        foreach (var vm in Built) vm.Dispose();
        Drafts.Dispose();
        Directory.Dispose();
        Sessions.Dispose();
        Failures.Dispose();
        Lane.Dispose();
    }
}

using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Http;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// A local agent's pull requests and work context read the app's server, so the server coming back re-reads them
/// even though the daemon attach never dropped.
[NotInParallel("AvaloniaSession")]
public class WorkspaceServerReconnectTests {
    [Test]
    public Task The_server_coming_back_rereads_pull_requests_with_the_daemon_still_attached() => RunOnUiAsync(async () => {
        var daemon = new FakeDaemonClientService();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time);
        using var lane = new BehaviorSubject<ServerLaneStatus>(new(ServerLaneState.Connected));
        var vm = new WorkspaceViewModel("agent", daemon, NewActions(), new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(), time,
            new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader(),
            pullRequests: source, serverLane: lane);
        try {
            daemon.Agents.AddOrUpdate(Agent("agent", "claude", true, sessionId: "session"));
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => vm.PullRequests.CanReveal, what: "PR loaded");
            var lists = source.Lists;

            lane.OnNext(new(ServerLaneState.Retrying, "Connection refused", Failure: ConnectionFailure.Refused));
            lane.OnNext(new(ServerLaneState.Connected));

            await WaitUntilAsync(() => source.Lists > lists, what: "PR list read again on reconnect");
            await Assert.That(vm.PullRequests.CanReveal).IsTrue();
        } finally { await vm.TeardownAsync(); }
    });
}

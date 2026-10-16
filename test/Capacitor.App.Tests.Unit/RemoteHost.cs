using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The remote host's dependencies, held together so a test disposes them once. The lane is
/// never connected, so even a row that carries a session id dials nothing and the routing
/// assertions stay deterministic.
sealed class RemoteHost : IDisposable {
    readonly FakeServerLane _lane = new();
    readonly SessionAccessService _access;
    readonly FakePermissionService _permissions = new();

    public RemoteHost() => _access = new SessionAccessService(_lane, new FakeTimeProvider());

    public FakeAgentDirectory Directory { get; } = new();
    public FakeWorkContextSource WorkSource { get; } = new();

    // A separate overload rather than an optional parameter: the factory parameter takes this
    // as a method group, and that conversion needs an exact one-argument signature.
    public RemoteSessionViewModel New(string agentId) => New(agentId, null);

    /// A session id only where a test needs one: it is what a local row is compared against
    /// before the removal counts as an origin change rather than an ended session.
    public RemoteSessionViewModel New(string agentId, string? sessionId, IPullRequestSource? pullRequests = null, ComposerDrafts? drafts = null) {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = agentId, SessionId = sessionId, Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
            Vendor = "claude", RegisteredAt = DateTime.UtcNow,
        });
        Directory.Rows.AddOrUpdate(row);
        return new RemoteSessionViewModel(row, Directory, _access, _permissions, WorkspaceFixtures.NewActions(), _lane,
            (_, _) => Task.FromResult(new SessionDetailFetch(null)), new RecordingOpener(), new FakeTimeProvider(), WorkSource,
            () => new FakeTerminalSurface(), pullRequests: pullRequests, drafts: drafts);
    }

    public void Dispose() {
        _access.Dispose();
        _permissions.Dispose();
        Directory.Dispose();
    }
}

using System.Reactive;
using Capacitor.App.Services;
using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// What the main window holds in its one workspace slot, whichever origin the session has.
public interface ISessionWorkspace {
    string AgentId { get; }
    WorkContextViewModel WorkContext { get; }
    PullRequestContextViewModel? PullRequests { get; }
    void OpenPullRequest(PullRequestSubjectDto? subject = null);
    Task TeardownAsync();
    /// Fires once the session is over for good; a row that only left this lane is not an end.
    IObservable<Unit> Ended { get; }
    /// Gives the composer's unsent draft to the same session's workspace on the other lane.
    void HandDraftTo(AgentOrigin origin);
}

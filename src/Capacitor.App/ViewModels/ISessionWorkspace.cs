using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

/// What the main window holds in its one workspace slot, whichever origin the session has.
public interface ISessionWorkspace {
    string AgentId { get; }
    WorkContextViewModel WorkContext { get; }
    PullRequestContextViewModel? PullRequests { get; }
    void OpenPullRequest(PullRequestSubjectDto? subject = null);
    Task TeardownAsync();
    /// Takes the composer's unsent draft out of this workspace, which another lane's is replacing.
    string HandOffDraft();
    void AdoptDraft(string text);
}

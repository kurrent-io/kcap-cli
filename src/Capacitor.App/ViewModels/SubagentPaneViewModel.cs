using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One subagent's own transcript, shown in place of its session's chat until the user goes back. It
/// reads through its own feed and run rows, so the parent's are untouched while it is open.
public sealed class SubagentPaneViewModel : ReactiveObject {
    internal const string Notice = "this is a subagent's own transcript";

    public SubagentPaneViewModel(
            RunRow row, string parentTitle, AgentOrigin origin, string parentAgentId, string vendor, string feedKey,
            Func<string, IChatTranscriptFeed> openFeed, IUrlOpener opener, TimeProvider time, IPermissionService permissions,
            Action back) {
        Row         = row;
        ParentTitle = parentTitle;
        var session = row.WhenAnyValue(r => r.IsRunning)
            .Select(running => new ChatSessionInfo(
                running ? "running" : "ended", running ? "Running" : "Finished", vendor, null, null, null, !running, Notice, feedKey));
        // Its own key keeps the parent's pending cards, which are keyed by the parent, out of this pane.
        Chat = new ChatTabViewModel(
            $"{parentAgentId}/{row.AgentId}", origin, session, Observable.Return<string[]?>(null), new ReadOnlyChatInput(),
            new NoAttachmentUploader(), openFeed, opener, time, permissions, new SessionRuns(time));
        BackCommand = ReactiveCommand.Create(back);
    }

    public RunRow Row { get; }
    public string ParentTitle { get; }
    public ChatTabViewModel Chat { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }

    public Task TeardownAsync() {
        BackCommand.Dispose();
        return Chat.TeardownAsync();
    }
}

using System.Reactive;
using System.Reactive.Disposables.Fluent;
using Capacitor.Cli.Core.LocalIpc;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Where a session's edits land when the daemon gave it a worktree of its own: not in the checkout the
/// user picked, so the pane names the worktree and the branch and says how to bring the work back.
public sealed partial class WorkContextViewModel {
    /// The agent runs in a worktree the daemon made for it, apart from the repository's own checkout.
    public bool ShowsOwnWorktree =>
        _dto is { WorkLocation: WorkLocationText.Owned, RepoPath: { } repo }
        && WorktreePath is { } path
        && !CheckoutLabel.IsMain(path, repo);

    /// Opening a folder needs the worktree on this machine.
    public bool CanOpenWorktree => ShowsOwnWorktree && !_isRemote;

    public string? OwnWorktreeBranch => ShowsOwnWorktree && Branch != "—" ? Branch : null;

    public string? OwnWorktreeNote => !ShowsOwnWorktree ? null
        : $"Edits land in this worktree, not in your {Repository} checkout. To bring them back, "
          + (OwnWorktreeBranch is null ? "merge or cherry-pick its branch. " : "merge or cherry-pick this branch. ")
          + "When the session ends, kcap keeps both if they hold work found nowhere else.";

    void WatchOwnWorktree() =>
        this.WhenAnyValue(x => x.WorktreePath, x => x.RepositoryPath, x => x.Repository, x => x.Branch,
                (_, _, _, _) => Unit.Default)
            .Subscribe(_ => {
                this.RaisePropertyChanged(nameof(ShowsOwnWorktree));
                this.RaisePropertyChanged(nameof(CanOpenWorktree));
                this.RaisePropertyChanged(nameof(OwnWorktreeBranch));
                this.RaisePropertyChanged(nameof(OwnWorktreeNote));
            })
            .DisposeWith(_disposables);
}

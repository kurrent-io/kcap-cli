using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class WhatsNextCardViewModel : ReactiveObject {
    bool _isSelected;
    bool _isLaunching;

    public WhatsNextCardViewModel(
            string targetKey,
            string group,
            string arm,
            string title,
            string subtitle,
            string tooltipText,
            string launchPrompt,
            string? repoHash = null,
            string? workItemId = null,
            string? sessionId = null,
            string? worktreePath = null,
            bool continuesInSameWorktree = false,
            Action<WhatsNextCardViewModel>? onSelect = null) {
        TargetKey               = targetKey;
        Group                   = group;
        Arm                     = arm;
        Title                   = title;
        Subtitle                = subtitle;
        TooltipText             = tooltipText;
        LaunchPrompt            = launchPrompt;
        RepoHash                = repoHash;
        WorkItemId              = workItemId;
        SessionId               = sessionId;
        WorktreePath            = worktreePath;
        ContinuesInSameWorktree = continuesInSameWorktree;
        SelectCommand           = ReactiveCommand.Create(() => onSelect?.Invoke(this));
    }

    public string  TargetKey               { get; }
    public string  Group                   { get; }
    public string  Arm                     { get; }
    public string  Title                   { get; }
    public string  Subtitle                { get; }
    public string  TooltipText             { get; }
    public string  LaunchPrompt            { get; }
    public string? RepoHash                { get; }
    public string? WorkItemId              { get; }
    public string? SessionId               { get; }
    public string? WorktreePath            { get; }
    public bool    ContinuesInSameWorktree { get; }

    public bool IsSelected {
        get => _isSelected;
        internal set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public bool IsLaunching {
        get => _isLaunching;
        internal set => this.RaiseAndSetIfChanged(ref _isLaunching, value);
    }

    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
}

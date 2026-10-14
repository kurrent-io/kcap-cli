namespace Capacitor.App.ViewModels;

public sealed record WorktreeModeChoice(WorktreeLaunchMode Mode, string Label, string Description) {
    public static readonly IReadOnlyList<WorktreeLaunchMode> All = [
        WorktreeLaunchMode.Automatic,
        WorktreeLaunchMode.SameWorktree,
        WorktreeLaunchMode.NoBranch,
    ];
}

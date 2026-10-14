namespace Capacitor.App.ViewModels;

public enum WorktreeLaunchMode {
    Automatic,
    SameWorktree,
    NoBranch,
}

public static class WorktreeLaunchModeExtensions {
    public static string ChipLabel(this WorktreeLaunchMode mode, bool continuesInExistingWorktree = false) => mode switch {
        WorktreeLaunchMode.Automatic    => continuesInExistingWorktree ? "Same worktree · automatic" : "New worktree · automatic",
        WorktreeLaunchMode.SameWorktree => "Same worktree",
        WorktreeLaunchMode.NoBranch     => "No branch until you ask for one",
        _                               => "New worktree · automatic",
    };

    public static string MenuLabel(this WorktreeLaunchMode mode) => mode switch {
        WorktreeLaunchMode.Automatic    => "New worktree · automatic",
        WorktreeLaunchMode.SameWorktree => "Same worktree",
        WorktreeLaunchMode.NoBranch     => "No branch until you ask for one",
        _                               => "New worktree · automatic",
    };

    public static string Description(this WorktreeLaunchMode mode) => mode switch {
        WorktreeLaunchMode.Automatic    => "Creates an isolated worktree, or continues in an existing one",
        WorktreeLaunchMode.SameWorktree => "Runs in the selected checkout without creating a new worktree",
        WorktreeLaunchMode.NoBranch     => "Explores directly in the checkout without creating a branch",
        _                               => "",
    };
}

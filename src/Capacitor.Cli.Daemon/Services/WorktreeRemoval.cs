namespace Capacitor.Cli.Daemon.Services;

/// <summary>What <see cref="WorktreeManager.RemoveAsync"/> did: removed the worktree, or kept it and
/// its branch because they held work that exists nowhere else.</summary>
public sealed record WorktreeRemoval(string? KeptBecause) {
    public static readonly WorktreeRemoval Removed = new((string?)null);

    public static WorktreeRemoval Keep(string reason) => new(reason);

    public bool Kept => KeptBecause is not null;
}

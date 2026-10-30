using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

public static class PinnedSessionRow {
    /// An empty access level is unknown: the read-only workspace acquires its lease and the server decides.
    public static RepoSessionDto From(ActivitySessionDto summary, PinPresentation presentation) {
        var repo = summary.Repositories?.FirstOrDefault(r => r.IsPrimary) ?? summary.Repositories?.FirstOrDefault();
        return new() {
            SessionId = summary.SessionId,
            Slug = summary.Slug,
            Title = summary.Title,
            Vendor = summary.Vendor,
            Cwd = summary.Cwd,
            Status = summary.Status,
            AccessLevel = "",
            StartedAt = summary.StartedAt,
            EndedAt = summary.EndedAt,
            LastActivityAt = summary.LastEventAt ?? summary.EndedAt ?? summary.StartedAt,
            Branch = summary.RepoBranch,
            PrimaryRepoHash = repo?.RepoHash,
            Repo = repo is null ? null : new() { Hash = repo.RepoHash, Owner = repo.Owner, Name = repo.RepoName },
            Owner = summary.OwnerUserId is { } owner
                ? new() { UserId = owner, Username = presentation.Username, DisplayName = presentation.DisplayName, AvatarUrl = presentation.AvatarUrl }
                : null,
        };
    }
}

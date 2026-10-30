using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class PinnedSessionRowTests {
    static readonly DateTimeOffset Started = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Maps_the_primary_repository_and_owner_presentation() {
        var summary = new ActivitySessionDto {
            SessionId = "s1", Slug = "brave-otter", Title = "Fix it", Vendor = "claude", Cwd = "/w",
            Status = "ended", StartedAt = Started, EndedAt = Started.AddHours(1), LastEventAt = Started.AddMinutes(50),
            RepoBranch = "main", OwnerUserId = "u-7",
            Repositories = [
                new() { RepoHash = "h-other", Owner = "acme", RepoName = "lib", IsPrimary = false },
                new() { RepoHash = "h-main", Owner = "acme", RepoName = "app", IsPrimary = true },
            ],
        };

        var row = PinnedSessionRow.From(summary, new PinPresentation("alice", "Alice A", "https://a/avatar"));

        await Assert.That(row.SessionId).IsEqualTo("s1");
        await Assert.That(row.Slug).IsEqualTo("brave-otter");
        await Assert.That(row.Title).IsEqualTo("Fix it");
        await Assert.That(row.Vendor).IsEqualTo("claude");
        await Assert.That(row.Cwd).IsEqualTo("/w");
        await Assert.That(row.Status).IsEqualTo("ended");
        await Assert.That(row.StartedAt).IsEqualTo(Started);
        await Assert.That(row.EndedAt).IsEqualTo(Started.AddHours(1));
        await Assert.That(row.LastActivityAt).IsEqualTo(Started.AddMinutes(50));
        await Assert.That(row.Branch).IsEqualTo("main");
        await Assert.That(row.PrimaryRepoHash).IsEqualTo("h-main");
        await Assert.That(row.Repo).IsEqualTo(new RepoSessionRepositoryDto { Hash = "h-main", Owner = "acme", Name = "app" });
        await Assert.That(row.Owner).IsEqualTo(new RepoSessionOwnerDto {
            UserId = "u-7", Username = "alice", DisplayName = "Alice A", AvatarUrl = "https://a/avatar",
        });
    }

    [Test]
    public async Task Leaves_owner_null_without_an_owner_id_and_access_level_empty() {
        var summary = new ActivitySessionDto {
            SessionId = "s2", Status = "active", StartedAt = Started,
            Repositories = [new() { RepoHash = "h-first" }, new() { RepoHash = "h-second" }],
        };

        var row = PinnedSessionRow.From(summary, new PinPresentation("alice", "Alice", null));

        await Assert.That(row.Owner).IsNull();
        await Assert.That(row.AccessLevel).IsEqualTo("");
        await Assert.That(row.Stale).IsFalse();
        await Assert.That(row.LastActivityAt).IsEqualTo(Started);
        await Assert.That(row.PrimaryRepoHash).IsEqualTo("h-first");
    }
}

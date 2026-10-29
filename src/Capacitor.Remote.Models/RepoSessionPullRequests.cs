using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The PR links a listing row carried. Items is null where the server withheld them, below full
/// access. Equal by content, so an unchanged row re-read every few seconds is not a change.
[JsonConverter(typeof(RepoSessionPullRequestsJsonConverter))]
public sealed class RepoSessionPullRequests(IReadOnlyList<RepoSessionPullRequestDto>? items) : IEquatable<RepoSessionPullRequests> {
    public static readonly RepoSessionPullRequests Withheld = new(null);

    public IReadOnlyList<RepoSessionPullRequestDto>? Items { get; } = items;

    public bool Equals(RepoSessionPullRequests? other) =>
        other is not null && (Items is null ? other.Items is null : other.Items is not null && Items.SequenceEqual(other.Items));

    public override bool Equals(object? obj) => Equals(obj as RepoSessionPullRequests);

    public override int GetHashCode() => Items?.Count ?? -1;
}

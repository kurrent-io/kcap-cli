using System.Collections.Immutable;

namespace Capacitor.Cli.Core;

/// <summary>
/// The commits kcap's git hook filed for a watcher, held until the batch carrying them is delivered.
/// </summary>
public abstract record CommitObservation {
    CommitObservation() { }

    /// <summary>
    /// Null exactly when not observed: the server tells that apart from none landed.
    /// </summary>
    public abstract ObservedCommit[]? Pending { get; }

    /// <summary>
    /// Set once the hook stops covering the session mid-way, such as switched off: batches go back
    /// to saying nothing about commits, so the server reads them from the transcript again.
    /// </summary>
    public bool Lapsed { get; private init; }

    /// <summary>
    /// A session that started uncovered stays so: its commits are already read from the transcript.
    /// </summary>
    public CommitObservation Rechecked(bool covered) => this is Uncovered ? this : this with { Lapsed = !covered };

    public virtual CommitObservation Collect() => this;

    /// <summary>
    /// A commit collected after the batch was built rides the next one.
    /// </summary>
    public virtual CommitObservation Delivered(ObservedCommit[]? sent) => this;

    /// <summary>
    /// The hook does not cover this session (git before 2.54, hook disabled, or the agent
    /// process now runs another session). Batches say nothing about commits, so the server keeps
    /// reading them from the transcript's git commands.
    /// </summary>
    public sealed record Uncovered : CommitObservation {
        public override ObservedCommit[]? Pending => null;
    }

    /// <summary>
    /// A subagent. It commits through its parent's agent process, so the hook files those
    /// commits under the parent. Batches report none, so the server reads no commits from its lines.
    /// </summary>
    public sealed record Subagent : CommitObservation {
        public override ObservedCommit[]? Pending => Lapsed ? null : [];
    }

    /// <summary>
    /// A main session the hook covers. Batches carry the commits the hook filed for it,
    /// and the server takes them instead of reading the transcript's git commands.
    /// </summary>
    public sealed record Covered(SessionCommits Commits) : CommitObservation {
        long Read { get; init; }

        ImmutableList<ObservedCommit> Held { get; init; } = [];

        /// <summary>
        /// Commits held when the hook lapsed still go.
        /// </summary>
        public override ObservedCommit[]? Pending => Lapsed && Held.IsEmpty ? null : [.. Held];

        public override CommitObservation Collect() {
            var (filed, next) = Commits.ReadFrom(Read);

            return this with { Read = next, Held = Held.AddRange(filed) };
        }

        public override CommitObservation Delivered(ObservedCommit[]? sent) => this with { Held = Held.RemoveRange(sent ?? []) };
    }
}

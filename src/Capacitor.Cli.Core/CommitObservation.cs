namespace Capacitor.Cli.Core;

/// <summary>
/// The commits a watcher's lines show landing, held until the batch carrying them is delivered.
/// <see cref="None"/> observes nothing: its batches carry no <c>observed_commits</c>, so the server
/// reads their commits from the lines' shell commands instead.
/// </summary>
public abstract class CommitObservation {
    CommitObservation() { }

    public static readonly CommitObservation None = new Unobserved();

    public static CommitObservation Of(Func<string, ShellSteps?> readSteps, CommitObserver observer, Func<string, string> redactMessage) =>
        new Observed(readSteps, observer, redactMessage);

    /// <summary>Null exactly when commits are not observed; empty when none landed.</summary>
    public abstract ObservedCommit[]? Pending { get; }

    /// <summary>Takes lines before redaction, which swaps an oversized one, such as a noisy
    /// pre-commit hook's result, for a placeholder.</summary>
    public abstract Task ObserveAsync(IEnumerable<string> rawLines);

    /// <summary>Lines before a resume point: a quiet commit is seen only when its call is known.</summary>
    public abstract void Recall(IEnumerable<string> rawLines);

    public abstract void Delivered();

    sealed class Unobserved : CommitObservation {
        public override ObservedCommit[]? Pending => null;

        public override Task ObserveAsync(IEnumerable<string> rawLines) => Task.CompletedTask;

        public override void Recall(IEnumerable<string> rawLines) { }

        public override void Delivered() { }
    }

    sealed class Observed(Func<string, ShellSteps?> readSteps, CommitObserver observer, Func<string, string> redactMessage) : CommitObservation {
        // A failed send re-reads its lines, so the same commit arrives again.
        readonly List<ObservedCommit> _pending = [];

        public override ObservedCommit[] Pending => [.. _pending];

        public override async Task ObserveAsync(IEnumerable<string> rawLines) {
            foreach (var line in rawLines)
                if (readSteps(line) is { } steps)
                    foreach (var commit in await observer.ObserveAsync(steps))
                        if (commit with { Message = redactMessage(commit.Message) } is var safe && !_pending.Contains(safe))
                            _pending.Add(safe);
        }

        public override void Recall(IEnumerable<string> rawLines) {
            foreach (var line in rawLines)
                if (readSteps(line) is { } steps) observer.Recall(steps);
        }

        public override void Delivered() => _pending.Clear();
    }
}

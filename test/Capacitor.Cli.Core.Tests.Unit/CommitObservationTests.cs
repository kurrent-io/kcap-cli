namespace Capacitor.Cli.Core.Tests.Unit;

public class CommitObservationTests {
    [TempDir] public required TempDir Tmp { get; init; }

    SessionCommits Commits => field ??= new(Tmp.PathTo("commits.jsonl"));

    static ObservedCommit Commit(string sha) => new() { Sha = sha, Message = "Fix watcher" };

    [Test]
    public async Task A_commit_is_held_until_delivered() {
        CommitObservation observation = new CommitObservation.Covered(Commits);

        Commits.Append(Commit("aaa"));
        observation = observation.Collect();
        await Assert.That(observation.Pending!.Single().Sha).IsEqualTo("aaa");

        observation = observation.Delivered(observation.Pending).Collect();
        await Assert.That(observation.Pending).IsEmpty();
    }

    [Test]
    public async Task Delivery_drops_only_what_the_batch_carried() {
        CommitObservation observation = new CommitObservation.Covered(Commits);

        Commits.Append(Commit("aaa"));
        observation = observation.Collect();
        var sent = observation.Pending;

        Commits.Append(Commit("bbb"));
        observation = observation.Collect().Delivered(sent);

        await Assert.That(observation.Pending!.Single().Sha).IsEqualTo("bbb");
    }

    [Test]
    public async Task A_lapsed_hook_sends_what_it_holds_then_reports_nothing_until_it_returns() {
        CommitObservation observation = new CommitObservation.Covered(Commits);

        Commits.Append(Commit("aaa"));
        observation = observation.Collect().Rechecked(covered: false);
        await Assert.That(observation.Pending!.Single().Sha).IsEqualTo("aaa");

        observation = observation.Delivered(observation.Pending);
        await Assert.That(observation.Pending).IsNull();
        await Assert.That(observation.Rechecked(covered: true).Pending).IsEmpty();
    }
}

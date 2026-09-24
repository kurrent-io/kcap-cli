namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>A batch says "not observed" with no commit set at all and "none landed" with an empty
/// one; the server reads the lines' shell commands only for the first.</summary>
public class CommitObservationTests {
    const string Full = "abc1234def5678abc1234def5678abc1234def56";
    const string Line = "a commit's result";

    [TempDir] public required TempDir Tmp { get; init; }

    CommitObservation Observing() => CommitObservation.Of(
        line => line == Line ? new ShellSteps(Tmp.Path, null, [], [new("t1", "[main abc1234] Fix watcher", IsError: false)]) : null,
        new CommitObserver(
            (arguments, _, _) => Task.FromResult(arguments switch {
                "rev-parse --verify --quiet abc1234^{commit}" => Full,
                $"log -1 --format=%s {Full}"                  => "Fix watcher",
                "rev-parse --show-toplevel"                   => Tmp.Path,
                _                                             => null
            }),
            _ => Task.FromResult<(string?, string?)>(("acme", "widgets"))),
        message => message);

    [Test]
    public async Task An_unobserving_watcher_reports_no_commit_set() {
        await CommitObservation.None.ObserveAsync([Line]);

        await Assert.That(CommitObservation.None.Pending).IsNull();
    }

    [Test]
    public async Task A_commit_is_held_once_until_delivered() {
        var observation = Observing();

        await Assert.That(observation.Pending).IsEmpty();

        await observation.ObserveAsync([Line, "unrelated", Line]);
        await Assert.That(observation.Pending!.Single().Sha).IsEqualTo(Full);

        observation.Delivered();
        await Assert.That(observation.Pending).IsEmpty();
    }
}

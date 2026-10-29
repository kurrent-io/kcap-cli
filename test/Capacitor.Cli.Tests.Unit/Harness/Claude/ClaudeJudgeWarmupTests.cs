namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;

public class ClaudeJudgeWarmupTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    static readonly TimeSpan[] NoWait = [TimeSpan.Zero];

    static PolicySnapshot Snapshot(string yaml) => new("snap-1", [
        new PolicyScopeDocument(PolicyScope.User, "/home/approvals.yaml", yaml,
            PolicyDocumentBinder.Bind(yaml, PolicyScope.User))], false, []);

    string Transcript() => Tmp.CreateFile("transcript.jsonl", string.Join("\n", Enumerable.Range(1, 5).Select(n =>
        new JsonObject {
            ["type"] = "user", ["uuid"] = $"00000000-0000-0000-0000-{n:D12}", ["promptId"] = $"p{n}",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = $"message {n}" },
        }.ToJsonString())) + "\n");

    string StatePath => ClaudeJudgeDeclarationReader.StatePath(Config.Root, Sid);

    [Test]
    public async Task A_judged_session_is_read_into_the_state_its_seams_resume_from() {
        new PolicySnapshotStore(Config.Root).Save(Sid, Snapshot("version: 1\njudge:\n  mode: unmatched\n"));
        var transcript = Transcript();

        var reads = await ClaudeJudgeWarmup.RunAsync(Config.Root, Sid, transcript, null, TimeProvider.System, CancellationToken.None, NoWait);

        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(File.ReadAllText(StatePath)).Contains($"\"offset\":{new FileInfo(transcript).Length}");
    }

    /// <summary>The no-judge world pays nothing: no scan and no state file.</summary>
    [Test]
    public async Task A_session_whose_policy_does_not_enable_the_judge_is_left_alone() {
        new PolicySnapshotStore(Config.Root).Save(Sid, Snapshot("version: 1\nrules: []\n"));

        var reads = await ClaudeJudgeWarmup.RunAsync(Config.Root, Sid, Transcript(), null, TimeProvider.System, CancellationToken.None, NoWait);

        await Assert.That(reads).IsEqualTo(0);
        await Assert.That(File.Exists(StatePath)).IsFalse();
    }

    [Test]
    public async Task A_session_with_no_frozen_snapshot_is_left_alone() {
        var reads = await ClaudeJudgeWarmup.RunAsync(Config.Root, Sid, Transcript(), null, TimeProvider.System, CancellationToken.None, NoWait);

        await Assert.That(reads).IsEqualTo(0);
        await Assert.That(File.Exists(StatePath)).IsFalse();
    }

    /// <summary>The session-start hook freezes the snapshot after it spawns the watcher.</summary>
    [Test]
    public async Task A_snapshot_frozen_after_the_watcher_starts_is_still_found() {
        var transcript = Transcript();
        var waits = new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(10) };

        var run = ClaudeJudgeWarmup.RunAsync(Config.Root, Sid, transcript, null, TimeProvider.System, CancellationToken.None, waits);
        new PolicySnapshotStore(Config.Root).Save(Sid, Snapshot("version: 1\njudge:\n  mode: unmatched\n"));

        await Assert.That(await run).IsEqualTo(1);
    }

    [Test]
    public async Task A_dashed_session_id_shares_the_seams_dashless_state() {
        new PolicySnapshotStore(Config.Root).Save(Sid, Snapshot("version: 1\njudge:\n  mode: unmatched\n"));

        await ClaudeJudgeWarmup.RunAsync(Config.Root, "9dc27753-7645-4e46-91ec-c2d69973c152", Transcript(), null,
            TimeProvider.System, CancellationToken.None, NoWait);

        await Assert.That(File.Exists(StatePath)).IsTrue();
    }
}

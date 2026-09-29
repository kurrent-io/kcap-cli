namespace Capacitor.Cli.Core.Tests.Unit;

public class SessionCommitsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_half_written_line_waits() {
        var path = Tmp.CreateFile("commits.jsonl", """{"sha":"aaa","message":"Fix"}""" + "\n" + """{"sha":"bbb","mess""");

        var (commits, next) = new SessionCommits(path).ReadFrom(0);
        await File.AppendAllTextAsync(path, "age\":\"Add\"}\n");

        await Assert.That(commits.Single().Sha).IsEqualTo("aaa");
        await Assert.That(new SessionCommits(path).ReadFrom(next).Commits.Single().Sha).IsEqualTo("bbb");
    }

    [Test]
    public async Task A_record_cut_short_loses_only_itself() {
        var commits = new SessionCommits(Tmp.CreateFile("commits.jsonl", """{"sha":"aaa","mess"""));

        commits.Append(new ObservedCommit { Sha = "bbb", Message = "Add" });

        await Assert.That(commits.ReadFrom(0).Commits.Single().Sha).IsEqualTo("bbb");
    }
}

namespace Capacitor.Cli.Core.Tests.Unit;

public class CommitInboxTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_half_written_line_waits() {
        var path = Tmp.PathTo("commits.jsonl");
        await File.WriteAllTextAsync(path, """{"sha":"aaa","message":"Fix"}""" + "\n" + """{"sha":"bbb","mess""");

        var (commits, next) = new CommitInbox(path).ReadFrom(0);
        await File.AppendAllTextAsync(path, "age\":\"Add\"}\n");

        await Assert.That(commits.Single().Sha).IsEqualTo("aaa");
        await Assert.That(new CommitInbox(path).ReadFrom(next).Commits.Single().Sha).IsEqualTo("bbb");
    }
}

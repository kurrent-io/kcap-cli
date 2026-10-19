using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

[ExcludeOn(OS.Windows)]
public class LocalLogFilesTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => new(Tmp.Path);

    [Test]
    public async Task No_logs_directory_has_no_files() =>
        await Assert.That(LocalLogFiles.Current(Root)).IsEmpty();

    [Test]
    public async Task The_newest_rolling_log_comes_first_and_an_empty_output_log_is_skipped() {
        var older = Tmp.CreateFile("logs/server-20261017.log", "old\n");
        var newer = Tmp.CreateFile("logs/server-20261018.log", "new\n");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));
        Tmp.CreateFile("logs/server.out.log", "");

        await Assert.That(LocalLogFiles.Current(Root)).IsEquivalentTo([newer]);
    }

    [Test]
    public async Task An_output_log_with_content_follows_the_rolling_log() {
        var rolling = Tmp.CreateFile("logs/server-20261018.log", "x\n");
        var output  = Tmp.CreateFile("logs/server.out.log", "Unhandled exception\n");

        await Assert.That(LocalLogFiles.Current(Root)).IsEquivalentTo([rolling, output], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(3)]
    [Arguments(200_000)]
    public async Task Tail_returns_the_last_lines_of_a_file_longer_than_one_block(int count) {
        var lines = Enumerable.Range(1, 20_000).Select(i => $"line {i}").ToArray();
        var path  = Tmp.CreateFile("logs/server.out.log", string.Join("\r\n", lines) + "\r\n");

        var tail = LocalLogFiles.Tail(path, count);

        await Assert.That(tail).IsEquivalentTo(lines[^Math.Min(count, lines.Length)..], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Tail_keeps_a_last_line_with_no_newline() =>
        await Assert.That(LocalLogFiles.Tail(Tmp.CreateFile("x.log", "a\nb\nc"), 2)).IsEquivalentTo(["b", "c"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
}

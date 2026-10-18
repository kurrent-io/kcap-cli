using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

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
}

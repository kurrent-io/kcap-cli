using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.Harness.Kiro;

public class KiroSessionTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Title_reads_as_auto() {
        var path = Tmp.CreateFile("session.json", """{"title":"t"}""");

        var title = new KiroSessionTitle(path).Read();

        await Assert.That(title!.Title).IsEqualTo("t");
        await Assert.That(title.Kind).IsEqualTo(HarnessTitleKind.Auto);
    }

    [Test]
    public async Task Blank_title_reads_null() {
        await Assert.That(new KiroSessionTitle(Tmp.CreateFile("session.json", """{"title":""}""")).Read()).IsNull();
    }

    [Test]
    public async Task Missing_file_reads_null() {
        await Assert.That(new KiroSessionTitle(Tmp.PathTo("absent.json")).Read()).IsNull();
    }

    [Test]
    public async Task Does_not_record_change_time() {
        await Assert.That(new KiroSessionTitle(Tmp.PathTo("absent.json")).RecordsChangeTime).IsFalse();
    }

    [Test]
    public async Task Reads_while_a_writer_holds_the_file() {
        var path = Tmp.CreateFile("session.json", """{"title":"live"}""");

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        await Assert.That(new KiroSessionTitle(path).Read()!.Title).IsEqualTo("live");
    }
}

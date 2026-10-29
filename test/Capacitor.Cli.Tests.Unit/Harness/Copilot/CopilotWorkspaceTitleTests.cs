using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.Copilot;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

public class CopilotWorkspaceTitleTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task User_named_reads_as_a_rename() {
        var path = Tmp.CreateFile("workspace.yaml", "name: proj\nuser_named: true\n");

        var title = new CopilotWorkspaceTitle(path).Read();

        await Assert.That(title!.Title).IsEqualTo("proj");
        await Assert.That(title.Kind).IsEqualTo(HarnessTitleKind.Rename);
    }

    [Test]
    public async Task Auto_generated_reads_as_auto() {
        var path = Tmp.CreateFile("workspace.yaml", "name: proj\nuser_named: false\n");

        var title = new CopilotWorkspaceTitle(path).Read();

        await Assert.That(title!.Kind).IsEqualTo(HarnessTitleKind.Auto);
    }

    [Test]
    public async Task No_name_reads_null() {
        await Assert.That(new CopilotWorkspaceTitle(Tmp.CreateFile("workspace.yaml", "cwd: /work\n")).Read()).IsNull();
    }

    [Test]
    public async Task Missing_file_reads_null() {
        await Assert.That(new CopilotWorkspaceTitle(Tmp.PathTo("absent.yaml")).Read()).IsNull();
    }

    [Test]
    public async Task Does_not_record_change_time() {
        await Assert.That(new CopilotWorkspaceTitle(Tmp.PathTo("absent.yaml")).RecordsChangeTime).IsFalse();
    }

    [Test]
    public async Task Reads_while_a_writer_holds_the_file() {
        var path = Tmp.CreateFile("workspace.yaml", "name: proj\nuser_named: true\n");

        using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        await Assert.That(new CopilotWorkspaceTitle(path).Read()!.Title).IsEqualTo("proj");
    }
}

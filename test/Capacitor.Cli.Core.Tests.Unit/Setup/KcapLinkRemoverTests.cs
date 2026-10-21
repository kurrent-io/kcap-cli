using System.Runtime.Versioning;
using Capacitor.Cli.Core.Setup;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class KcapLinkRemoverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Binary(string name = "bin-kcap") {
        Tmp.CreateFile(name, "x");

        return Tmp.GetResolvedPath(name);
    }

    string Folder() {
        Tmp.CreateDir("bin");

        return Tmp.GetResolvedPath("bin");
    }

    [Test]
    public async Task Removes_a_link_to_the_target() {
        var binary = Binary();
        var folder = Folder();
        var link   = Path.Combine(folder, "kcap");
        File.CreateSymbolicLink(link, binary);

        var remover = new KcapLinkRemover([folder], binary);

        await Assert.That(remover.Find()).IsEquivalentTo([link]);
        await Assert.That(remover.Remove()).IsEmpty();
        await Assert.That(new FileInfo(link).Exists || new FileInfo(link).LinkTarget is not null).IsFalse();
        await Assert.That(File.Exists(binary)).IsTrue();
    }

    [Test]
    public async Task Removes_a_link_through_a_chain_to_the_target() {
        var binary  = Binary();
        var current = Tmp.GetResolvedPath("current");
        File.CreateSymbolicLink(current, binary);
        var folder = Folder();
        var link   = Path.Combine(folder, "kcap");
        File.CreateSymbolicLink(link, current);

        var failures = new KcapLinkRemover([folder], binary).Remove();

        await Assert.That(failures).IsEmpty();
        await Assert.That(new FileInfo(link).LinkTarget).IsNull();
    }

    [Test]
    public async Task Keeps_a_link_to_another_binary() {
        var binary = Binary();
        var other  = Binary("other");
        var folder = Folder();
        var link   = Path.Combine(folder, "kcap");
        File.CreateSymbolicLink(link, other);

        var remover = new KcapLinkRemover([folder], binary);

        await Assert.That(remover.Find()).IsEmpty();
        await Assert.That(remover.Remove()).IsEmpty();
        await Assert.That(new FileInfo(link).LinkTarget).IsNotNull();
    }

    [Test]
    public async Task Keeps_a_regular_file_named_kcap() {
        var binary = Binary();
        var folder = Folder();
        var file   = Tmp.CreateFile(Path.Combine("bin", "kcap"), "x");

        await Assert.That(new KcapLinkRemover([folder], file).Find()).IsEmpty();
        await Assert.That(new KcapLinkRemover([folder], binary).Remove()).IsEmpty();
        await Assert.That(File.Exists(file)).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows), UnsupportedOSPlatform("windows")]
    public async Task Reports_a_link_it_cannot_remove() {
        var binary = Binary();
        var folder = Folder();
        var link   = Path.Combine(folder, "kcap");
        File.CreateSymbolicLink(link, binary);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try {
            var failures = new KcapLinkRemover([folder], binary).Remove();

            await Assert.That(failures.Select(f => f.Path)).IsEquivalentTo([link]);
        } finally {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

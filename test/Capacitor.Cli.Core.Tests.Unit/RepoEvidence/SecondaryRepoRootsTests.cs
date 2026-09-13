using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.RepoEvidence;

public class SecondaryRepoRootsTests {
    const string Primary = "/h/dev/server";

    static string? FakeFindRoot(string dir) =>
        dir.StartsWith("/h/dev/server", StringComparison.Ordinal) ? Primary
        : dir.StartsWith("/h/dev/cli-wt", StringComparison.Ordinal) ? "/h/dev/cli-wt"
        : dir.StartsWith("/h/dev/other", StringComparison.Ordinal) ? "/h/dev/other"
        : dir.StartsWith("/h/dev/third", StringComparison.Ordinal) ? "/h/dev/third"
        : null;

    static string Line(string tool, string key, string path) =>
        $$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"{{{tool}}}","input":{"{{{key}}}":"{{{path}}}"}}]}}""";

    static SecondaryRepoRoots New(int capacity = 8) => new(FakeFindRoot, Primary, capacity);

    [Test]
    public async Task Mutation_in_another_checkout_records_its_root() {
        var roots = New();

        roots.OnLine("claude", Line("Edit", "file_path", "/h/dev/cli-wt/src/x.cs"));

        await Assert.That(roots.Roots).IsEquivalentTo(["/h/dev/cli-wt"]);
    }

    [Test]
    public async Task Mutation_inside_the_primary_root_is_ignored() {
        var roots = New();

        roots.OnLine("claude", Line("Write", "file_path", "/h/dev/server/src/x.cs"));

        await Assert.That(roots.Roots).IsEmpty();
    }

    [Test]
    public async Task Reads_never_record_a_root() {
        var roots = New();

        roots.OnLine("claude", Line("Read", "file_path", "/h/dev/cli-wt/src/x.cs"));
        roots.OnLine("claude", Line("Grep", "path", "/h/dev/other"));

        await Assert.That(roots.Roots).IsEmpty();
    }

    [Test]
    public async Task Paths_outside_any_checkout_are_ignored() {
        var roots = New();

        roots.OnLine("claude", Line("Edit", "file_path", "/tmp/scratch/x.cs"));

        await Assert.That(roots.Roots).IsEmpty();
    }

    [Test]
    public async Task Other_vendors_are_ignored() {
        var roots = New();

        roots.OnLine("codex", Line("Edit", "file_path", "/h/dev/cli-wt/src/x.cs"));

        await Assert.That(roots.Roots).IsEmpty();
    }

    [Test]
    public async Task A_root_is_recorded_once_and_the_cap_holds() {
        var roots = New(capacity: 2);

        roots.OnLine("claude", Line("Edit", "file_path", "/h/dev/cli-wt/a.cs"));
        roots.OnLine("claude", Line("Edit", "file_path", "/h/dev/cli-wt/b.cs"));
        roots.OnLine("claude", Line("Edit", "file_path", "/h/dev/other/c.cs"));
        roots.OnLine("claude", Line("Edit", "file_path", "/h/dev/third/d.cs"));

        await Assert.That(roots.Roots).IsEquivalentTo(["/h/dev/cli-wt", "/h/dev/other"]);
    }

    [Test]
    public async Task Malformed_lines_fail_open() {
        var roots = New();

        roots.OnLine("claude", "{not json");
        roots.OnLine("claude", Line("Edit", "file_path", "/h/dev/cli-wt/a.cs"));

        await Assert.That(roots.Roots).IsEquivalentTo(["/h/dev/cli-wt"]);
    }

    [Test]
    public async Task Without_a_primary_root_every_checkout_is_secondary() {
        var roots = new SecondaryRepoRoots(FakeFindRoot, primaryRoot: null);

        roots.OnLine("claude", Line("Edit", "file_path", "/h/dev/server/src/x.cs"));

        await Assert.That(roots.Roots).IsEquivalentTo([Primary]);
    }
}

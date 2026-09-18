namespace Capacitor.Cli.Core.Tests.Unit;

public class CanonicalPathTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task An_ancestor_symlink_is_resolved_not_just_the_leaf() {
        Tmp.CreateDir("real/inner");
        var linked = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(linked, Tmp.GetResolvedPath("real"));

        var resolved = CanonicalPath.Resolve(Path.Combine(linked, "inner"));

        await Assert.That(resolved).IsEqualTo(Tmp.GetResolvedPath("real/inner"));
    }

    [Test]
    public async Task Containment_follows_links_out_of_the_boundary() {
        var boundary = Tmp.CreateDir("repo");
        var outside  = Tmp.CreateDir("outside");
        var escape   = Path.Combine(boundary, "escape");
        Directory.CreateSymbolicLink(escape, outside);

        await Assert.That(CanonicalPath.IsWithin(Path.Combine(boundary, "inside"), boundary)).IsTrue();
        await Assert.That(CanonicalPath.IsWithin(escape, boundary)).IsFalse();
        await Assert.That(CanonicalPath.IsWithin(Path.Combine(escape, "deeper"), boundary)).IsFalse();
        await Assert.That(CanonicalPath.IsWithin(boundary, boundary)).IsTrue();
    }

    [Test]
    public async Task A_symlink_cycle_terminates() {
        var a = Tmp.PathTo("a");
        var b = Tmp.PathTo("b");
        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        await Assert.That(CanonicalPath.Resolve(Path.Combine(a, "x"))).IsNotNull();
    }
}

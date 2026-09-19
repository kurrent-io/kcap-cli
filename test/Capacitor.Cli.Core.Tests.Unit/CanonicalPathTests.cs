namespace Capacitor.Cli.Core.Tests.Unit;

public class CanonicalPathTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task An_ancestor_symlink_is_resolved_not_just_the_leaf() {
        Tmp.CreateDir("real/inner");
        var linked = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(linked, Tmp.GetResolvedPath("real"));

        var resolved = CanonicalPath.Resolve(Path.Combine(linked, "inner"));

        await Assert.That(resolved).IsEqualTo(Tmp.GetResolvedPath("real", "inner"));
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

    /// <summary>An ordinary component costs nothing, so no checkout is deep enough to spend the
    /// budget before the walk reaches a link that leaves the boundary.</summary>
    [Test]
    public async Task A_deep_path_does_not_spend_the_symlink_budget() {
        var boundary = Tmp.CreateDir("repo");
        var outside  = Tmp.CreateDir("outside");
        // Past the 40-hop budget on its own, before the temp root's own components are counted.
        var deep     = boundary.Nest(45);
        var escape   = deep.PathTo("escape");

        Directory.CreateSymbolicLink(escape, outside.Path);

        // Preconditions: the tree was actually built, and a path this deep is still resolvable — so
        // the refusal below is the link's and not the depth's.
        await Assert.That(Directory.Exists(deep)).IsTrue();
        await Assert.That(CanonicalPath.IsWithin(deep.PathTo("inner"), boundary)).IsTrue();
        await Assert.That(CanonicalPath.IsWithin(escape, boundary)).IsFalse();
    }

    /// <summary>A link target's <c>..</c> has to be applied to what the components before it really
    /// point at. Normalizing the target first applies it to unresolved text, which turns a link out
    /// of the boundary into a path that reads as inside it.</summary>
    [Test]
    public async Task A_dot_dot_inside_a_link_target_is_resolved_not_folded() {
        var boundary = Tmp.CreateDir("repo");
        var outside  = Tmp.CreateDir("outside");
        var vendor   = boundary.CreateDir(".agents");

        outside.CreateDir("child");
        outside.CreateDir("local-skills");
        Directory.CreateSymbolicLink(vendor.PathTo("hop"), outside.PathTo("child"));
        // Relative, so the OS reads it against the link's own directory: hop → outside/child, then
        // `..` → outside, then local-skills.
        Directory.CreateSymbolicLink(vendor.PathTo("skills"), Path.Combine("hop", "..", "local-skills"));

        // A precondition: this is what the filesystem itself says the link leads to.
        await Assert.That(Directory.Exists(vendor.PathTo("skills"))).IsTrue();

        await Assert.That(CanonicalPath.Resolve(vendor.PathTo("skills")))
            .IsEqualTo(Tmp.GetResolvedPath("outside", "local-skills"));
        await Assert.That(CanonicalPath.IsWithin(vendor.PathTo("skills"), boundary)).IsFalse();
    }

    /// <summary>The same ordering in the path handed in: folding its <c>..</c> before the walk
    /// starts steps over whatever the component ahead of it points at.</summary>
    [Test]
    public async Task A_dot_dot_in_the_given_path_is_resolved_not_folded() {
        var boundary = Tmp.CreateDir("repo");
        var outside  = Tmp.CreateDir("outside");

        outside.CreateDir("child");
        outside.CreateDir("sibling");
        Directory.CreateSymbolicLink(boundary.PathTo("hop"), outside.PathTo("child"));

        var through = boundary.PathTo("hop", "..", "sibling");

        await Assert.That(CanonicalPath.Resolve(through))
            .IsEqualTo(Tmp.GetResolvedPath("outside", "sibling"));
        await Assert.That(CanonicalPath.IsWithin(through, boundary)).IsFalse();
    }

    /// <summary>A cycle exhausts the budget, and what a walk gave up on is a name rather than a
    /// location — so containment refuses it even though the remainder reads as inside.</summary>
    [Test]
    public async Task A_symlink_cycle_is_not_within_anything() {
        var boundary = Tmp.CreateDir("repo");
        var a        = boundary.PathTo("a");
        var b        = boundary.PathTo("b");

        Directory.CreateSymbolicLink(a, b);
        Directory.CreateSymbolicLink(b, a);

        await Assert.That(CanonicalPath.TryResolve(Path.Combine(a, "x"), out _)).IsFalse();
        await Assert.That(CanonicalPath.IsWithin(Path.Combine(a, "x"), boundary)).IsFalse();
    }
}

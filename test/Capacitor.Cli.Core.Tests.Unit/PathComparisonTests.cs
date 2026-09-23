namespace Capacitor.Cli.Core.Tests.Unit;

public class PathComparisonTests {
    /// <summary>The choice follows the platform's default filesystem, and the volume under the test
    /// root is what proves which one that is: a probe that merely restated the implementation would
    /// pass whichever way the implementation went.</summary>
    [Test]
    public async Task Two_casings_compare_the_way_the_filesystem_reads_them() {
        using var tmp = new TempDir();
        var created   = tmp.CreateDir("Repo");
        var sameName  = tmp.PathTo("REPO");

        await Assert.That(PathComparison.Equal(created, sameName)).IsEqualTo(Directory.Exists(sameName));
        await Assert.That(PathComparison.Comparer.Equals(created.Path, sameName))
            .IsEqualTo(Directory.Exists(sameName));
        await Assert.That(PathComparison.Equal(created, tmp.PathTo("Other"))).IsFalse();
    }

    [Test]
    public async Task Containment_reads_an_alternative_casing_the_same_way() {
        using var tmp = new TempDir();
        var boundary  = tmp.CreateDir("Repo");
        var inside    = tmp.PathTo("REPO", "inner");

        await Assert.That(CanonicalPath.IsWithin(inside, boundary))
            .IsEqualTo(Directory.Exists(tmp.PathTo("REPO")));
    }
}

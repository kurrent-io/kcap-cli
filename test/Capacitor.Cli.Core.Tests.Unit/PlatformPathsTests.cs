namespace Capacitor.Cli.Core.Tests.Unit;

public class PlatformPathsTests {
    [Test]
    public async Task Trailing_separator_is_ignored_by_the_comparer() {
        await Assert.That(PlatformPaths.Comparer.Equals("/a/b", "/a/b/")).IsTrue();
        await Assert.That(PlatformPaths.Comparer.GetHashCode("/a/b")).IsEqualTo(PlatformPaths.Comparer.GetHashCode("/a/b/"));
    }

    [Test]
    public async Task Leaf_is_the_last_segment_after_normalization() {
        await Assert.That(PlatformPaths.Leaf("/a/b/")).IsEqualTo("b");
        await Assert.That(PlatformPaths.Normalize("/a/b/")).IsEqualTo("/a/b");
    }

    [Test]
    public async Task Case_rule_follows_the_platform() {
        var equal = PlatformPaths.Comparer.Equals("/A", "/a");
        await Assert.That(equal).IsEqualTo(!OperatingSystem.IsLinux());
    }
}

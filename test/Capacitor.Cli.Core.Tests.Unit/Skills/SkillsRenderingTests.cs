using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class SkillsRenderingTests {
    static SkillSnapshotItem Item(string slug) => new() {
        DocId = Guid.NewGuid(), Slug = slug, Title = "T", Description = "When to use.", Body = "Body.",
        Version = 1, ContentHash = "h1",
    };

    [Test]
    [Arguments("retry-rules-ab12cd34", true)]
    [Arguments("a", true)]
    [Arguments("", false)]
    [Arguments("has/slash", false)]
    [Arguments("has\\backslash", false)]
    [Arguments("..", false)]
    [Arguments("Upper-Case", false)]
    [Arguments("dot.name", false)]
    public async Task Slug_safety_admits_only_single_lowercase_segments(string slug, bool safe) {
        await Assert.That(SkillsRendering.IsSafeSlug(slug)).IsEqualTo(safe);
    }

    [Test]
    public async Task Renders_frontmatter_with_a_quoted_description() {
        var text = SkillsRendering.RenderSkillFile(Item("retry-rules") with {
            Description = "Use when \"retrying\" appends:\nafter a conflict.",
        });
        await Assert.That(text.StartsWith("---\nname: retry-rules\n", StringComparison.Ordinal)).IsTrue();
        await Assert.That(text).Contains("description: \"Use when \\\"retrying\\\" appends:\\nafter a conflict.\"");
        await Assert.That(text.EndsWith("---\n\nBody.\n", StringComparison.Ordinal)).IsTrue();
    }
}

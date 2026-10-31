using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A <c>*</c> in the to path is refused, and when it was typed to mean "and everything under
/// it" the refusal offers the literal prefix rule that already does that.</summary>
public class RemapWildcardSuggestionTests {
    [Test]
    public async Task A_trailing_wildcard_on_both_sides_suggests_the_two_prefixes() {
        var error = RemapCommand.WildcardToError(@"C:\work\*", @"C:\old\*", @"C:\work\*");

        await Assert.That(error).Contains("the to path is literal");
        await Assert.That(error).Contains(@"kcap remap C:\old C:\work");
    }

    [Test]
    public async Task A_literal_from_keeps_its_path_in_the_suggestion() {
        var error = RemapCommand.WildcardToError("~/work/*", "~/old", "~/work/*");

        await Assert.That(error).Contains("kcap remap ~/old ~/work");
    }

    [Test]
    public async Task A_wildcard_in_the_middle_gets_no_suggestion() {
        var error = RemapCommand.WildcardToError("~/work/*/src", "~/old/*", "~/work/*/src");

        await Assert.That(error).DoesNotContain("does what you meant");
    }

    [Test]
    public async Task A_path_with_spaces_is_quoted_in_the_suggestion() {
        var error = RemapCommand.WildcardToError("~/My Work/*", "~/old", "~/My Work/*");

        await Assert.That(error).Contains("kcap remap ~/old '~/My Work'");
    }

    [Test]
    public async Task A_path_with_a_shell_metacharacter_is_quoted_in_the_suggestion() {
        var error = RemapCommand.WildcardToError("~/R&D/*", "~/old", "~/R&D/*");

        await Assert.That(error).Contains("kcap remap ~/old '~/R&D'");
    }

    [Test]
    public async Task An_apostrophe_cannot_end_the_quoted_path_early() {
        var error = RemapCommand.WildcardToError("~/Bob's/*", "~/old", "~/Bob's/*");

        await Assert.That(error).Contains(OperatingSystem.IsWindows()
            ? "kcap remap ~/old '~/Bob''s'"
            : @"kcap remap ~/old '~/Bob'\''s'");
    }
}

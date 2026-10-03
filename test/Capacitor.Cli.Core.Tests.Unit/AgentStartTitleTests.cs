namespace Capacitor.Cli.Core.Tests.Unit;

public class AgentStartTitleTests {
    [Test]
    public async Task Explicit_title_wins_and_is_not_derived() {
        var t = AgentStartTitle.ForLocalStart("  Fix login  ", ["fix the login redirect"]);

        await Assert.That(t).IsEqualTo(new AgentStartTitle("Fix login", Derived: false));
    }

    [Test]
    public async Task Last_bare_argument_is_the_derived_prompt() {
        await Assert.That(AgentStartTitle.ForLocalStart(null, ["fix X"]))
            .IsEqualTo(new AgentStartTitle("fix X", Derived: true));
    }

    [Test]
    public async Task Prompt_after_a_complete_flag_pair_is_still_found() {
        await Assert.That(AgentStartTitle.ForLocalStart(null, ["--model", "opus", "fix X"]))
            .IsEqualTo(new AgentStartTitle("fix X", Derived: true));
    }

    [Test]
    public async Task Argument_right_after_a_flag_may_be_its_value_and_is_not_taken() {
        await Assert.That(AgentStartTitle.ForLocalStart(null, ["-p", "fix X"])).IsNull();
    }

    [Test]
    public async Task Trailing_flag_is_not_a_prompt() {
        await Assert.That(AgentStartTitle.ForLocalStart(null, ["fix X", "--verbose"])).IsNull();
    }

    [Test]
    public async Task No_passthrough_and_no_title_gives_none() {
        await Assert.That(AgentStartTitle.ForLocalStart(null, [])).IsNull();
    }

    [Test]
    public async Task Blank_prompt_gives_none() {
        await Assert.That(AgentStartTitle.ForLocalStart(null, ["  \n "])).IsNull();
    }

    [Test]
    public async Task Multi_line_prompt_takes_its_first_non_blank_line() {
        await Assert.That(AgentStartTitle.ForLocalStart(null, ["\n  Fix the build  \nthen run tests"]))
            .IsEqualTo(new AgentStartTitle("Fix the build", Derived: true));
    }

    [Test]
    public async Task Long_prompt_is_cut_to_80_characters_with_an_ellipsis() {
        var t = AgentStartTitle.ForLocalStart(null, [new string('a', 200)]);

        await Assert.That(t!.Value.Text).IsEqualTo(new string('a', 79) + "…");
        await Assert.That(t.Value.Text.Length).IsEqualTo(AgentStartTitle.MaxDerivedLength);
    }

    [Test]
    public async Task Cut_never_splits_a_surrogate_pair() {
        var line = new string('a', 78) + "😀" + new string('b', 10);

        var shortened = AgentStartTitle.Shorten(line)!;

        await Assert.That(shortened).IsEqualTo(new string('a', 78) + "…");
    }

    [Test]
    public async Task Prompt_of_exactly_80_characters_is_kept_whole() {
        var line = new string('a', 80);

        await Assert.That(AgentStartTitle.Shorten(line)).IsEqualTo(line);
    }
}

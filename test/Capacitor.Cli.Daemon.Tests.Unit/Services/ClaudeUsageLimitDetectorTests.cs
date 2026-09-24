using System.Text;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Pins the live-screen match for Claude's usage-limit menu: the title plus two known choices,
/// cleared when that screen is erased, and quiet for prose that only quotes a choice.
public class ClaudeUsageLimitDetectorTests {
    const string Menu =
        "You've hit your session limit · resets 3:10pm\n\n" +
        "What do you want to do?\n\n" +
        "1. Stop and wait for limit to reset\n" +
        "2. Wait here, then continue automatically shortly\n" +
        "3. Ask your admin for more usage\n";

    static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Test]
    public async Task Menu_on_the_screen_is_a_blocked_question_with_the_printed_choices() {
        var notice = new ClaudeUsageLimitDetector().Observe(Utf8(Menu));

        await Assert.That(notice).IsNotNull();
        await Assert.That(notice!.Kind).IsEqualTo(UsageLimitKinds.Blocked);
        await Assert.That(notice.Summary).IsEqualTo("You've hit your session limit · resets 3:10pm");
        await Assert.That(notice.Prompt).IsEqualTo("What do you want to do?");
        await Assert.That(notice.Options.Select(o => $"{o.Index}:{o.Label}").ToArray()).IsEquivalentTo(new[] {
            "1:Stop and wait for limit to reset",
            "2:Wait here, then continue automatically shortly",
            "3:Ask your admin for more usage",
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Cursor_and_color_around_the_same_menu_still_match() {
        var framed = "\x1b[31m" + Menu.Replace("1. ", "\x1b[0m❯ 1. ") + "\x1b[0m";
        var notice = new ClaudeUsageLimitDetector().Observe(Utf8(framed));

        await Assert.That(notice).IsNotNull();
        await Assert.That(notice!.Options).Count().IsEqualTo(3);
    }

    [Test]
    public async Task A_menu_split_across_chunks_matches_once_the_second_choice_arrives() {
        var detector = new ClaudeUsageLimitDetector();
        var split = Menu.IndexOf("2. ", StringComparison.Ordinal);

        await Assert.That(detector.Observe(Utf8(Menu[..split]))).IsNull();
        var notice = detector.Observe(Utf8(Menu[split..]));

        await Assert.That(notice).IsNotNull();
        await Assert.That(notice!.Options).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Clearing_the_screen_drops_the_question() {
        var detector = new ClaudeUsageLimitDetector();
        detector.Observe(Utf8(Menu));

        var notice = detector.Observe(Utf8("\x1b[2Jready\r\n"));

        await Assert.That(notice).IsNull();
    }

    [Test]
    public async Task Prose_that_quotes_one_choice_is_not_a_question() {
        var prose = "The menu says Stop and wait for limit to reset, or ask your admin for more usage.\n";

        await Assert.That(new ClaudeUsageLimitDetector().Observe(Utf8(prose))).IsNull();
    }

    [Test]
    public async Task A_title_with_only_one_known_choice_is_not_a_question() {
        var screen = "What do you want to do?\n1. Stop and wait for limit to reset\n";

        await Assert.That(ClaudeUsageLimitDetector.Parse(screen)).IsNull();
    }
}

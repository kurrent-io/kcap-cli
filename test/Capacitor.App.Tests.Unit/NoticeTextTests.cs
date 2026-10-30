using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class NoticeTextTests {
    /// The title is the first sentence without its full stop; an ellipsis and a host's dots stay.
    [Test]
    [Arguments(null, null, null)]
    [Arguments("Connecting to the server…", "Connecting to the server…", null)]
    [Arguments("Your sign-in has expired.", "Your sign-in has expired", null)]
    [Arguments("The daemon isn't running. kcap is trying to start it.", "The daemon isn't running", "kcap is trying to start it.")]
    [Arguments("It's connected to norton.kcap.ai, not kcap.ai. Sign in.", "It's connected to norton.kcap.ai, not kcap.ai", "Sign in.")]
    public async Task Headline_splits_at_the_first_sentence(string? text, string? title, string? body) {
        var (t, b) = NoticeText.Headline(text);
        await Assert.That(t).IsEqualTo(title);
        await Assert.That(b).IsEqualTo(body);
    }

    /// Commands that end the text come out one per line in order, and the colon that introduced them
    /// becomes a full stop; a command with prose after it stays where it is.
    [Test]
    [Arguments("No commands here.", "No commands here.", null)]
    [Arguments("Run this, then restart kcap: `kcap a --b`", "Run this, then restart kcap.", "kcap a --b")]
    [Arguments("Run these: `kcap a` `kcap b`", "Run these.", "kcap a\nkcap b")]
    [Arguments("An unbalanced ` stays as written.", "An unbalanced ` stays as written.", null)]
    [Arguments("`kcap a`", null, "kcap a")]
    [Arguments("Run this: `kcap a`.", "Run this.", "kcap a")]
    [Arguments("Run `kcap --version` in a terminal, then try again.", "Run `kcap --version` in a terminal, then try again.", null)]
    public async Task Commands_are_lifted_out_of_the_prose(string text, string? prose, string? command) {
        var (p, c) = NoticeText.Commands(text);
        await Assert.That(p).IsEqualTo(prose);
        await Assert.That(c).IsEqualTo(command);
    }
}

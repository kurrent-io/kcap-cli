using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

public class ClaudeSessionArgsTests {
    const string Id = "0123abcd-0000-4000-8000-0123456789ab";

    [Test]
    [Arguments(new string[0], false, null)]
    [Arguments(new[] { "--model", "opus", "fix it" }, false, null)]
    [Arguments(new[] { "-p", "--debug", "fix it" }, false, null)]
    [Arguments(new[] { "--", "--resume", Id }, false, null)]
    [Arguments(new[] { "--session-id", Id }, true, Id)]
    [Arguments(new[] { "--session-id=" + Id, "fix it" }, true, Id)]
    [Arguments(new[] { "--resume", Id, "go on" }, true, Id)]
    [Arguments(new[] { "--model", "opus", "--resume=" + Id }, true, Id)]
    [Arguments(new[] { "--model=opus", "--resume", Id }, true, Id)]
    [Arguments(new[] { "--model=opus", "--session-id", Id }, true, Id)]
    [Arguments(new[] { "-r", Id }, true, Id)]
    [Arguments(new[] { "--resume", Id, "--fork-session" }, true, null)]
    [Arguments(new[] { "--resume" }, true, null)]
    [Arguments(new[] { "--resume", "the widget work" }, true, null)]
    [Arguments(new[] { "--session-id", "not-a-uuid" }, true, null)]
    [Arguments(new[] { "-c" }, true, null)]
    [Arguments(new[] { "--continue", "go on" }, true, null)]
    [Arguments(new[] { "-pc", "go on" }, true, null)]
    [Arguments(new[] { "--from-pr", "12" }, true, null)]
    [Arguments(new[] { "--teleport" }, true, null)]
    // Right after a flag, a session flag may be that flag's value: Claude then runs a fresh session with the
    // UUID as its prompt, so the UUID names nothing.
    [Arguments(new[] { "--system-prompt", "--resume", Id }, true, null)]
    [Arguments(new[] { "--system-prompt", "--resume=" + Id }, true, null)]
    public async Task Reads_whether_the_args_choose_the_session_and_which(string[] args, bool selects, string? sessionId) {
        await Assert.That(ClaudeSessionArgs.SelectsSession(args, out var chosen)).IsEqualTo(selects);
        await Assert.That(chosen).IsEqualTo(sessionId);
    }
}

using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeHelperRunTests {
    /// <summary>The id must pass any UUID validator a Claude build applies to <c>--session-id</c>, and be
    /// recognised in both the dashed form hooks carry and the dashless form discovery normalises to.</summary>
    [Test]
    public async Task A_new_session_id_is_a_version_4_uuid_recognised_dashed_and_dashless() {
        var id = ClaudeHelperRun.NewSessionId();

        await Assert.That(Guid.TryParseExact(id, "D", out var parsed)).IsTrue();
        await Assert.That(parsed.Version).IsEqualTo(4);
        await Assert.That("89ab".Contains(id[19])).IsTrue();
        await Assert.That(ClaudeHelperRun.IsHelperSessionId(id)).IsTrue();
        await Assert.That(ClaudeHelperRun.IsHelperSessionId(parsed.ToString("N"))).IsTrue();
        await Assert.That(ClaudeHelperRun.NewSessionId()).IsNotEqualTo(id);
    }

    /// <summary>The ids Claude assigns the user's own sessions are random v4 ids: none of them may read as kcap's.</summary>
    [Test]
    public async Task A_random_session_id_is_never_a_helper_id() {
        var flagged = Enumerable.Range(0, 10_000).Count(_ => ClaudeHelperRun.IsHelperSessionId(Guid.NewGuid().ToString()));

        await Assert.That(flagged).IsEqualTo(0);
        await Assert.That(ClaudeHelperRun.IsHelperSessionId(null)).IsFalse();
        await Assert.That(ClaudeHelperRun.IsHelperSessionId("agent-a1b2c3")).IsFalse();
    }

    [Test]
    [Arguments("/private/var/folders/x1/abc/T/kcap-claude-fd1e4d96f5b64380b6abc0f0f28cf83e")]
    [Arguments("/tmp/kcap-claude-fd1e4d96f5b64380b6abc0f0f28cf83e/")]
    [Arguments(@"C:\Users\me\AppData\Local\Temp\kcap-claude-fd1e4d96f5b64380b6abc0f0f28cf83e")]
    public async Task The_runners_own_working_directory_is_a_helper_directory(string cwd) {
        await Assert.That(ClaudeHelperRun.IsHelperWorkingDir(cwd)).IsTrue();
    }

    /// <summary>Only the exact shape the runner creates counts: a project that merely mentions it, a
    /// directory below it, or the name without its 32 hex digits is the user's.</summary>
    [Test]
    [Arguments("/home/me/src/kcap-cli")]
    [Arguments("/home/me/kcap-claude-cwd")]
    [Arguments("/home/me/my-kcap-claude-fd1e4d96f5b64380b6abc0f0f28cf83e")]
    [Arguments("/tmp/kcap-claude-fd1e4d96f5b64380b6abc0f0f28cf83e/repo")]
    [Arguments("/tmp/kcap-claude-FD1E4D96F5B64380B6ABC0F0F28CF83E")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Any_other_working_directory_is_the_users(string? cwd) {
        await Assert.That(ClaudeHelperRun.IsHelperWorkingDir(cwd)).IsFalse();
    }

    [Test]
    public async Task The_working_directory_name_carries_the_session_id() {
        var id = ClaudeHelperRun.NewSessionId();

        await Assert.That(ClaudeHelperRun.IsHelperWorkingDir(ClaudeHelperRun.WorkingDirName(id))).IsTrue();
        await Assert.That(ClaudeHelperRun.WorkingDirName(id)).IsEqualTo("kcap-claude-" + id.Replace("-", ""));
    }
}

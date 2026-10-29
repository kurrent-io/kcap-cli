using Capacitor.Cli.Harness.Grok;

namespace Capacitor.Cli.Tests.Unit.Harness.Grok;

/// <summary>
/// Process tables shaped as a Grok hook sees them. The daemon is pid 700 and spawns the hosted grok, pid
/// 100, directly; a hook command may run under the <c>sh -c</c> Grok wraps it in.
/// </summary>
public class GrokHostedSessionTests {
    const string Daemon = "700";

    static Func<int, (int ppid, IReadOnlyList<string> names)?> Table(params (int Pid, int Ppid, string Name)[] rows) =>
        pid => rows.Where(r => r.Pid == pid).Select(r => ((int, IReadOnlyList<string>)?)(r.Ppid, [r.Name])).FirstOrDefault();

    [Test]
    public async Task A_hook_run_directly_by_the_hosted_grok_is_daemon_hosted() {
        var table = Table((100, 700, "grok"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 100, table)).IsTrue();
    }

    [Test]
    public async Task A_hook_run_through_a_shell_by_the_hosted_grok_is_daemon_hosted() {
        var table = Table((90, 100, "sh"), (100, 700, "grok"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 90, table)).IsTrue();
    }

    /// <summary>A shell tool that execs grok leaves the nested grok a direct child of the hosted one.</summary>
    [Test]
    public async Task A_grok_the_hosted_agent_execs_is_not_daemon_hosted() {
        var table = Table((90, 200, "sh"), (200, 100, "grok"), (100, 700, "grok"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 90, table)).IsFalse();
    }

    [Test]
    public async Task A_grok_the_hosted_agent_starts_from_a_shell_is_not_daemon_hosted() {
        var table = Table((200, 150, "grok"), (150, 100, "zsh"), (100, 700, "grok"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 200, table)).IsFalse();
    }

    /// <summary><c>KCAP_GROK_PATH</c> may name a build with another process name.</summary>
    [Test]
    public async Task A_hosted_grok_under_another_process_name_is_daemon_hosted() {
        var table = Table((90, 100, "sh"), (100, 700, "xai-grok-pager"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 90, table)).IsTrue();
    }

    [Test]
    public async Task A_differently_named_grok_the_hosted_agent_execs_is_not_daemon_hosted() {
        var table = Table((90, 200, "sh"), (200, 100, "xai-grok-pager"), (100, 700, "grok"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 90, table)).IsFalse();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("1")]
    [Arguments("not-a-pid")]
    public async Task No_daemon_pid_means_not_hosted(string? marker) {
        var table = Table((100, 700, "grok"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(marker, hookParentPid: 100, table)).IsFalse();
    }

    [Test]
    public async Task A_hook_far_below_the_daemons_child_is_not_daemon_hosted() {
        var table = Table((80, 90, "node"), (90, 100, "sh"), (100, 700, "grok"), (700, 1, "kcap-daemon"));

        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 80, table)).IsFalse();
    }

    [Test]
    public async Task An_unreadable_process_table_means_not_hosted() {
        await Assert.That(GrokHostedSession.IsDaemonHosted(Daemon, hookParentPid: 100, _ => null)).IsFalse();
    }
}

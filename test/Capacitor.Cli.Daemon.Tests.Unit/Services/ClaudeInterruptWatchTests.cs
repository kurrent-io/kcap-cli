using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A PTY Claude agent's turn that ended by interrupt reads as waiting: Claude Code fires no
/// Stop hook on Esc, so its transcript's interrupt marker is the only record of it.</summary>
public class ClaudeInterruptWatchTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Interrupt     = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""";
    const string InterruptTool = """{"type":"user","message":{"role":"user","content":"[Request interrupted by user for tool use]"}}""";
    const string Prompt        = """{"type":"user","message":{"role":"user","content":"do the thing"}}""";
    const string Reply         = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"on it"}]}}""";
    const string SideInterrupt = """{"type":"user","isSidechain":true,"message":{"role":"user","content":"[Request interrupted by user]"}}""";
    const string Progress      = """{"type":"progress","data":{}}""";

    [Test]
    [Arguments(new[] { Reply, Interrupt }, true)]
    [Arguments(new[] { InterruptTool }, true)]
    [Arguments(new[] { Interrupt, Progress }, true)]
    [Arguments(new[] { Interrupt, Reply }, false)]
    [Arguments(new[] { Interrupt, Prompt }, false)]
    [Arguments(new[] { SideInterrupt }, false)]
    [Arguments(new[] { Progress, "not json" }, false)]
    public async Task The_last_turn_record_decides(string[] lines, bool interrupted) =>
        await Assert.That(ClaudeInterruptWatch.EndsInterrupted(lines)).IsEqualTo(interrupted);

    /// <summary>History before the watch starts is not this turn's ending.</summary>
    [Test]
    public async Task Only_a_marker_appended_after_the_watch_starts_counts() {
        var path = Tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, Interrupt + "\n");
        var time = new FakeTimeProvider();
        var fired = 0;
        using var cts = new CancellationTokenSource();
        var watch = ClaudeInterruptWatch.RunAsync(path, () => Interlocked.Increment(ref fired), time, TimeSpan.FromSeconds(1), cts.Token);

        time.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(50);
        await Assert.That(fired).IsEqualTo(0);

        await File.AppendAllTextAsync(path, Reply + "\n" + Interrupt + "\n");
        for (var i = 0; i < 100 && Volatile.Read(ref fired) == 0; i++) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
        }
        await Assert.That(fired).IsEqualTo(1);

        cts.Cancel();
        await watch;
    }

    /// <summary>Discovery starts the watch for a PTY Claude agent only.</summary>
    [Test]
    [Arguments("claude", true)]
    [Arguments("codex", false)]
    public async Task Discovery_watches_a_pty_claude_agents_transcript(string vendor, bool watched) {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var path  = Tmp.PathTo("session.jsonl");
        await File.WriteAllTextAsync(path, Prompt + "\n");
        var agent = orch.SeedAgentForTest("a1", vendor: vendor);

        await orch.RunDiscoveryForTest(agent, _ => ("0123456789abcdef0123456789abcdef", path));
        await File.AppendAllTextAsync(path, Interrupt + "\n");

        if (watched) await WaitHarness.PollUntilAsync(() => agent.ActivityClock.AwaitingInput);
        else await Task.Delay(TimeSpan.FromSeconds(2.5));
        await Assert.That(agent.ActivityClock.AwaitingInput).IsEqualTo(watched);
    }
}

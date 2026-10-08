using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// <c>kcap refresh</c> runs every step against its own binary, keeps going past a failed or stalled
/// one, and reports which did not finish. Only an install that owns its plugin directory gets the
/// <c>.mcp.json</c> patch.
/// </summary>
public class RefreshCommandTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class RecordingRunner(Func<string[], ProcessResult>? answer = null) : IProcessRunner {
        public List<(string File, string[] Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls.Add((fileName, args));
            return Task.FromResult(answer?.Invoke(args) ?? new ProcessResult(0, "", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task Every_step_runs_against_the_binary_in_order() {
        var runner = new RecordingRunner();
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await new RefreshCommand(runner).RunAsync("/x/bin/kcap", InstallKind.Unknown, () => null, stdout, stderr);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(runner.Calls.Select(c => c.File).Distinct().Single()).IsEqualTo("/x/bin/kcap");
        await Assert.That(runner.Calls.Select(c => string.Join(' ', c.Args)).ToArray())
            .IsEquivalentTo(RefreshCommand.Steps.Select(s => string.Join(' ', s)).ToArray());
        await Assert.That(runner.Calls[^1].Args).IsEquivalentTo(["daemon", "service", "refresh"]);
        await Assert.That(stdout.ToString()).Contains("Refreshed agent integrations.");
    }

    /// <summary>The list the install scripts and refresh.js carried: one entry per vendor, Claude last
    /// among the plugins, then the daemon units.</summary>
    [Test]
    public async Task The_steps_cover_every_vendor_and_the_daemon() {
        var vendors = RefreshCommand.Steps.Where(s => s[0] == "plugin").Select(s => s.Length == 4 ? s[2] : "claude").ToArray();

        await Assert.That(vendors).IsEquivalentTo(
            ["--skills", "--codex", "--cursor", "--copilot", "--pi", "--opencode", "--gemini", "--kiro", "--antigravity", "claude"]);
        await Assert.That(RefreshCommand.Steps.Where(s => s[0] == "plugin").All(s => s[^1] == "--if-installed")).IsTrue();
    }

    [Test]
    public async Task A_failed_or_stalled_step_does_not_stop_the_rest_and_is_reported() {
        var runner = new RecordingRunner(args => args.Contains("--codex") ? new ProcessResult(2, "", "boom\n", false)
            : args.Contains("--kiro") ? new ProcessResult(-1, "", "", true)
            : new ProcessResult(0, "", "", false));
        var (stdout, stderr) = (new StringWriter(), new StringWriter());

        var exit = await new RefreshCommand(runner).RunAsync("/x/bin/kcap", InstallKind.Unknown, () => null, stdout, stderr);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(runner.Calls.Count).IsEqualTo(RefreshCommand.Steps.Count);
        await Assert.That(stderr.ToString()).Contains("kcap plugin install --codex --if-installed: exit 2 (boom)");
        await Assert.That(stderr.ToString()).Contains("kcap plugin install --kiro --if-installed: timed out");
    }

    [Test]
    public async Task A_script_install_points_the_plugin_config_at_its_binary() {
        Tmp.CreateFile("plugin/.mcp.json", """{"mcpServers":{"kcap-review":{"command":"kcap","args":["mcp","review"]}}}""");

        await new RefreshCommand(new RecordingRunner())
            .RunAsync("/x/bin/kcap", InstallKind.Script, () => Tmp.PathTo("plugin"), new StringWriter(), new StringWriter());

        await Assert.That(File.ReadAllText(Tmp.PathTo("plugin", ".mcp.json"))).Contains("\"/x/bin/kcap\"");
    }

    /// <summary>A dev build resolves the repository's own plugin directory, which must not be rewritten.</summary>
    [Test]
    public async Task An_unknown_install_leaves_the_plugin_config_alone() {
        const string shipped = """{"mcpServers":{"kcap-review":{"command":"kcap","args":["mcp","review"]}}}""";
        Tmp.CreateFile("plugin/.mcp.json", shipped);

        await new RefreshCommand(new RecordingRunner())
            .RunAsync("/x/bin/kcap", InstallKind.Unknown, () => Tmp.PathTo("plugin"), new StringWriter(), new StringWriter());

        await Assert.That(File.ReadAllText(Tmp.PathTo("plugin", ".mcp.json"))).IsEqualTo(shipped);
    }
}

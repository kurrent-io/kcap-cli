using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Grok runs the hooks Claude Code's and Cursor's configs declare, so kcap's <c>kcap hook --claude</c>
/// and <c>kcap hook --cursor</c> fire on every Grok event with Grok's payload. Such an invocation
/// must leave no trace: no hook-activity stamp, no post or spool, no output.
///
/// <para>Runs the real binary, because the guard sits at the composition root ahead of anything an
/// in-process test can reach. The control case runs the same invocation without Grok's variable and
/// must stamp — otherwise the absence asserted under Grok proves nothing.</para>
/// </summary>
public class ForeignHookUnderGrokTests : IDisposable {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    readonly List<Process> _spawned = [];

    public void Dispose() {
        foreach (var p in _spawned) { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } p.Dispose(); }
    }

    string GrokPreToolUsePayload() => new JsonObject {
        ["hookEventName"]   = "pre_tool_use",
        ["hook_event_name"] = "PreToolUse",
        ["sessionId"]       = Guid.NewGuid().ToString(),
        ["cwd"]             = Config.Directory,
        ["workspaceRoot"]   = Config.Directory,
        ["toolName"]        = "run_terminal_command",
        ["toolInput"]       = new JsonObject { ["command"] = "ls" },
        ["toolUseId"]       = "call-1",
    }.ToJsonString();

    string ActivityStamp(string vendor) => Config.Root.Path("hook-activity", vendor);

    [Test]
    [Arguments("claude")]
    [Arguments("cursor")]
    public async Task A_foreign_vendor_hook_run_by_grok_exits_zero_and_leaves_no_trace(string vendor) {
        var r = await RunAsync(vendor, GrokPreToolUsePayload(), grokHookEvent: "pre_tool_use");

        await Assert.That(r.ExitCode).IsEqualTo(0);
        await Assert.That(r.Stdout).IsEmpty();
        await Assert.That(File.Exists(ActivityStamp(vendor))).IsFalse();
        await Assert.That(Directory.EnumerateFileSystemEntries(Config.Directory, "*", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    [Arguments("claude")]
    [Arguments("cursor")]
    public async Task The_same_hook_outside_grok_stamps_its_vendor_activity(string vendor) {
        await RunAsync(vendor, GrokPreToolUsePayload(), grokHookEvent: null);

        await Assert.That(File.Exists(ActivityStamp(vendor))).IsTrue();
    }

    /// <summary>A Claude session started beneath a Grok hook inherits <c>GROK_HOOK_EVENT</c>; its
    /// own hooks carry Claude's payload and must still run.</summary>
    [Test]
    public async Task A_claude_hook_with_an_inherited_grok_variable_still_runs() {
        var claudePayload = new JsonObject {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"]      = Guid.NewGuid().ToString(),
            ["cwd"]             = Config.Directory,
            ["tool_name"]       = "Bash",
            ["tool_input"]      = new JsonObject { ["command"] = "ls" },
        }.ToJsonString();

        await RunAsync("claude", claudePayload, grokHookEvent: "pre_tool_use");

        await Assert.That(File.Exists(ActivityStamp("claude"))).IsTrue();
    }

    /// <summary>Codex rejects a hook that prints nothing, so a nested Codex session's hook must still
    /// answer with its handshake.</summary>
    [Test]
    public async Task A_codex_hook_with_an_inherited_grok_variable_still_answers() {
        var codexPayload = new JsonObject {
            ["hook_event_name"] = "Stop",
            ["session_id"]      = Guid.NewGuid().ToString(),
        }.ToJsonString();

        var r = await RunAsync("codex", codexPayload, grokHookEvent: "stop");

        await Assert.That(r.Stdout).Contains("\"continue\":true");
    }

    async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string vendor, string stdin, string? grokHookEvent) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "hook", $"--{vendor}", "--no-update-check");
        psi.WorkingDirectory = Config.Directory;
        // Refused at once, so the control case reaches dispatch without waiting on a network.
        psi.Environment["KCAP_URL"]             = "http://127.0.0.1:1";
        psi.Environment["KCAP_NO_UPDATE_CHECK"] = "1";
        psi.Environment["KCAP_SESSION_ID"]      = "";
        psi.Environment.Remove("KCAP_SKIP");
        psi.Environment.Remove("CLAUDE_ENV_FILE");

        if (grokHookEvent is null) {
            psi.Environment.Remove("GROK_HOOK_EVENT");
        } else {
            psi.Environment["GROK_HOOK_EVENT"] = grokHookEvent;
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start kcap");
        _spawned.Add(process);

        // A hook that exits early can close the pipe before the payload is written.
        try {
            await process.StandardInput.WriteAsync(stdin);
        } catch (IOException) {
        } finally {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return (process.ExitCode, await stdout, await stderr);
    }
}

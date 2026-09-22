using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// GATED containment cert: drives the real <c>pi --mode rpc</c> argv
/// <see cref="PiRpcHostedAgentRuntimeFactory.BuildPsi(Capacitor.Cli.Daemon.DaemonConfig,Capacitor.Cli.Daemon.Services.RuntimeStartContext,PiReviewerLaunchPaths,IReadOnlyList{PiReviewerTool})"/>
/// builds and the real reviewer extension against a scripted loopback provider, proving a reviewer is
/// offered exactly its allowlisted tool surface, inherits no operator or repository context, and
/// cannot read, list or search across the worktree boundary. One canary per containment source, plus
/// hostile tool calls forced by the script rather than left to a model to volunteer or decline — a
/// declined call proves nothing about whether the tool would have run.
///
/// <para>Zero cost: no authentication, no server, no model request — the provider is a local scripted
/// stand-in and the result channel is a stub MCP server over stdio.</para>
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class PiReviewerContainmentCertTests {
    const string Gate = "KCAP_PI_REVIEWER_CONTAINMENT";

    static void RequireGate() {
        Skip.Unless(Environment.GetEnvironmentVariable(Gate) == "1",
            $"Gated: drives a real `pi --mode rpc` against a scripted local provider — set {Gate}=1. "
          + "Needs only `pi` and `node` on PATH; no authentication and no model cost.");
        Skip.Unless(!OperatingSystem.IsWindows(), "The reviewer lane is POSIX-only.");
    }

    static readonly PiScriptedStep[] HostileCalls = [
        new(Tool: "bash",  ArgsJson: """{"command":"touch BASH_RAN"}""", Id: "c1"),
        new(Tool: "write", ArgsJson: """{"path":"WRITE_RAN","content":"x"}""", Id: "c2"),
        new(Tool: "edit",  ArgsJson: """{"path":"inside.txt","oldText":"INSIDE-FILE","newText":"EDITED"}""", Id: "c3"),
        new(Tool: "read",  ArgsJson: """{"path":"inside.txt"}""", Id: "c4"),
        new(Tool: "grep",  ArgsJson: """{"pattern":"INSIDE"}""", Id: "c5"),
        new(Tool: "find",  ArgsJson: """{"pattern":"*"}""", Id: "c6"),
        new(Tool: "ls",    ArgsJson: """{"path":"."}""", Id: "c7"),
    ];

    [Test]
    public async Task The_reviewer_is_offered_exactly_its_tool_surface_and_inherits_nothing() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: true);

        var run = await bench.RunAsync(argvOverride: null, new PiScriptedStep(Text: "ok"));

        await Assert.That(run.Ready).IsTrue();
        await Assert.That(run.ToolsOffered).IsEquivalentTo(
            new[] { "read_file", "list_directory", "search_files", "submit_review_result", "send_flow_message" });
        await Assert.That(run.FirstRequestText).DoesNotContain("INV-");
        await Assert.That(run.FirstRequestText).Contains("You are a code reviewer running unattended");
    }

    [Test]
    public async Task No_Pi_built_in_can_be_called() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);

        var run = await bench.RunAsync(null, [.. HostileCalls, new PiScriptedStep(Text: "done")]);

        foreach (var tool in new[] { "bash", "write", "edit", "read", "grep", "find", "ls" })
            await Assert.That(run.ToolResults).Contains(r => r.Contains($"Tool {tool} not found"));
        await Assert.That(File.Exists(Path.Combine(bench.Worktree, "BASH_RAN"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(bench.Worktree, "WRITE_RAN"))).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(bench.Worktree, "inside.txt"))).Contains("INSIDE-FILE");
    }

    [Test]
    public async Task The_same_calls_execute_without_the_allowlist() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);

        // Positive control: proves a scripted call reaches an executor when the tool exists, so the
        // not-found results above are containment and not a driver that never calls anything. Two
        // built-ins, not one, so "not found" for write/edit/read/grep/find/ls doesn't rest entirely on
        // bash's own control.
        await bench.RunAsync(argvOverride: ["--mode", "rpc", "--no-approve", "--no-extensions", "--offline"],
            HostileCalls[0], HostileCalls[1], new PiScriptedStep(Text: "done"));

        await Assert.That(File.Exists(Path.Combine(bench.Worktree, "BASH_RAN"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(bench.Worktree, "WRITE_RAN"))).IsTrue();
    }

    [Test]
    public async Task Read_file_accepts_an_inside_path() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);

        var run = await bench.RunAsync(null,
            new PiScriptedStep(Tool: "read_file", ArgsJson: """{"path":"inside.txt"}""", Id: "r1"),
            new PiScriptedStep(Text: "done"));

        await Assert.That(run.ToolResults).Contains(r => r.Contains("INSIDE-FILE"));
        await Assert.That(string.Join("\n", run.ToolResults)).DoesNotContain("path is outside the repository under review");
    }

    [Test]
    [Arguments("""{"path":"OUTSIDE_ABS"}""")]
    [Arguments("""{"path":"../outside/secret.txt"}""")]
    [Arguments("""{"path":"SIBLING_PREFIX"}""")]
    [Arguments("""{"path":"leak"}""")]
    [Arguments("""{"path":"sub/linkdir/secret.txt"}""")]
    public async Task Read_file_refuses_every_path_that_resolves_outside_the_root(string args) {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);
        var resolved = args.Replace("OUTSIDE_ABS", Path.Combine(bench.Outside, "secret.txt"))
                           .Replace("SIBLING_PREFIX", bench.Worktree + "-evil/secret.txt");

        var run = await bench.RunAsync(null, new PiScriptedStep(Tool: "read_file", ArgsJson: resolved, Id: "r1"),
            new PiScriptedStep(Text: "done"));

        await Assert.That(run.ToolResults).Contains(r => r.Contains("path is outside the repository under review"));
        await Assert.That(string.Join("\n", run.ToolResults)).DoesNotContain("OUTSIDE-SECRET");
    }

    [Test]
    public async Task Listing_and_searching_never_cross_a_symlink() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);

        var run = await bench.RunAsync(null,
            new PiScriptedStep(Tool: "list_directory", ArgsJson: """{"path":"sub/linkdir"}""", Id: "l1"),
            new PiScriptedStep(Tool: "search_files",   ArgsJson: """{"text":"OUTSIDE-SECRET"}""", Id: "s1"),
            new PiScriptedStep(Tool: "search_files",   ArgsJson: """{"text":"INSIDE-FILE"}""", Id: "s2"),
            new PiScriptedStep(Text: "done"));

        var results = string.Join("\n", run.ToolResults);
        await Assert.That(results).Contains("path is outside the repository under review");   // l1
        await Assert.That(results).DoesNotContain("secret.txt:");                              // s1 found nothing through a link
        await Assert.That(results).Contains("inside.txt:1:");                                  // s2, the positive control
    }

    [Test]
    public async Task The_result_channel_delivers_its_arguments() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);

        await bench.RunAsync(null,
            new PiScriptedStep(Tool: "submit_review_result", ArgsJson: """{"round_token":"tok-1","kind":"clean"}""", Id: "x1"),
            new PiScriptedStep(Text: "done"));

        await Assert.That(File.ReadAllText(bench.ResultLog)).Contains("tok-1");
    }

    [Test]
    public async Task A_repository_cannot_choose_where_the_session_is_written_and_no_package_is_installed() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: true);

        var run = await bench.RunAsync(null, new PiScriptedStep(Text: "ok"));

        await Assert.That(run.SessionFile!).StartsWith(PiReviewerLaunchDir.RootFor(bench.StateDir));
        await Assert.That(Directory.Exists(bench.HostileSessionDir)).IsFalse();

        // Evidence, not silence: the npmCommand stand-in really is invoked under --offline (Pi spawns
        // it once for "root -g" before the extension filter even applies), so its absence of an
        // "install" line means the install was refused — not that npmCommand was never reached at all.
        await Assert.That(bench.NpmInvocations).IsNotEmpty();
        await Assert.That(bench.NpmInvocations).Contains(i => i.Contains("root"));
        await Assert.That(bench.NpmInvocations).DoesNotContain(i => i.StartsWith("install", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_canaries_are_live_without_the_suppression_flags() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: true);

        // Positive control for the first test: every plant must be observable, or "no INV- token"
        // proves nothing. Bench planting differs from probe.py's, so this asserts EVERY
        // token-bearing source individually rather than trusting that a couple of them standing in
        // for the rest — a source silently dead in THIS bench's planting would not otherwise be caught.
        var run = await bench.RunAsync(argvOverride: ["--mode", "rpc", "--approve", "--offline"], new PiScriptedStep(Text: "ok"));

        await Assert.That(run.ToolsOffered).Contains("opext_tool");
        await Assert.That(run.ToolsOffered).Contains("repoext_tool");

        // AGENTS.md, at both scopes.
        await Assert.That(run.FirstRequestText).Contains("INV-OP-AGENTS");
        await Assert.That(run.FirstRequestText).Contains("INV-REPO-AGENTS");

        // An ancestor directory's CLAUDE.md.
        await Assert.That(run.FirstRequestText).Contains("INV-REPO-ANCESTOR-CLAUDE");

        // The repository's own SYSTEM.md / APPEND_SYSTEM.md. The operator's own APPEND_SYSTEM.md is
        // deliberately NOT asserted here: the probe findings record it as shadowed by the repository's
        // when both are present in the same launch — measured on its own is a --system-prompt/
        // --append-system-prompt argv test, not this inventory control.
        await Assert.That(run.FirstRequestText).Contains("INV-REPO-SYSTEM");
        await Assert.That(run.FirstRequestText).Contains("INV-REPO-APPEND-SYSTEM");

        // Skill description tokens, at every planted scope.
        await Assert.That(run.FirstRequestText).Contains("INV-OP-SKILL-DECLARED");
        await Assert.That(run.FirstRequestText).Contains("INV-OP-SKILL-AGENTDIR");
        await Assert.That(run.FirstRequestText).Contains("INV-OP-SKILL-HOME-AGENTS");
        await Assert.That(run.FirstRequestText).Contains("INV-REPO-SKILL-PI");
        await Assert.That(run.FirstRequestText).Contains("INV-REPO-SKILL-AGENTS");
        await Assert.That(run.FirstRequestText).Contains("INV-REPO-SKILL-ANCESTOR");

        // Prompt templates (INV-OP-TEMPLATE, INV-REPO-TEMPLATE) are planted too, but their only
        // observable is the get_commands RPC, never the first request's text — this control cannot
        // prove them live; their liveness rests on the committed probe's own get_commands-based
        // control instead.
    }

    [Test]
    public async Task A_manifest_tool_the_server_does_not_serve_fails_the_launch_before_any_request() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false, resultServerServesSubmit: false);

        var run = await bench.RunAsync(null, new PiScriptedStep(Text: "never sent"));

        await Assert.That(run.Ready).IsFalse();
        await Assert.That(bench.Provider.Requests).IsEmpty();
        await Assert.That(run.Stderr).Contains("does not serve submit_review_result");
    }
}

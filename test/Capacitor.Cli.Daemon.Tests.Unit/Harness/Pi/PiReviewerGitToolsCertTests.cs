using System.Diagnostics;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>
/// GATED, like <see cref="PiReviewerContainmentCertTests"/>: the reviewer's read-only git tools, driven
/// through a real <c>pi --mode rpc</c> and the real reviewer extension against a real repository. The
/// hostile calls are forced by the script, so a refusal is the tool's and not a model declining.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class PiReviewerGitToolsCertTests {
    const string Gate = "KCAP_PI_REVIEWER_CONTAINMENT";

    static void RequireGate() {
        Skip.Unless(Environment.GetEnvironmentVariable(Gate) == "1",
            $"Gated: drives a real `pi --mode rpc` against a scripted local provider — set {Gate}=1. "
          + "Needs `pi`, `node` and `git` on PATH; no authentication and no model cost.");
        Skip.Unless(!OperatingSystem.IsWindows(), "The reviewer lane is POSIX-only.");
    }

    [Test]
    public async Task The_git_tools_read_history_the_checked_out_files_cannot_show() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);
        CommitTwoRevisions(bench.Worktree);

        var run = await bench.RunAsync(null,
            new PiScriptedStep(Tool: "git_log",  ArgsJson: """{}""", Id: "g1"),
            new PiScriptedStep(Tool: "git_diff", ArgsJson: """{"base":"HEAD~1","head":"HEAD"}""", Id: "g2"),
            new PiScriptedStep(Tool: "git_show", ArgsJson: """{"rev":"HEAD~1:inside.txt"}""", Id: "g3"),
            new PiScriptedStep(Text: "done"));

        await Assert.That(run.ToolResults).Contains(r => r.Contains("second revision"));
        await Assert.That(run.ToolResults).Contains(r => r.Contains("+SECOND-LINE"));
        await Assert.That(run.ToolResults).Contains(r => r.Contains("INSIDE-FILE") && !r.Contains("SECOND-LINE"));
    }

    [Test]
    public async Task The_git_tools_refuse_options_and_paths_outside_the_repository() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);
        CommitTwoRevisions(bench.Worktree);
        var planted = Path.Combine(bench.Outside, "PLANTED");

        var run = await bench.RunAsync(null,
            new PiScriptedStep(Tool: "git_diff", ArgsJson: $$"""{"base":"--output={{planted}}"}""", Id: "h1"),
            new PiScriptedStep(Tool: "git_log",  ArgsJson: """{"path":"../outside"}""", Id: "h2"),
            new PiScriptedStep(Tool: "git_show", ArgsJson: """{"rev":"HEAD:../outside/secret.txt"}""", Id: "h3"),
            new PiScriptedStep(Tool: "git_log",  ArgsJson: """{"range":"HEAD --all"}""", Id: "h4"),
            new PiScriptedStep(Text: "done"));

        var results = string.Join("\n", run.ToolResults);
        await Assert.That(results).Contains("base is not a revision");
        await Assert.That(results).Contains("range is not a revision");
        await Assert.That(results).Contains("path is outside the repository under review");
        await Assert.That(results).DoesNotContain("OUTSIDE-SECRET");
        await Assert.That(File.Exists(planted)).IsFalse();
    }

    /// <summary>The repository's own config can name external diff and textconv programs; the tools
    /// must never run them. The direct git calls at the end are the positive control: the same config
    /// does run both programs when those switches are absent.</summary>
    [Test]
    public async Task The_git_tools_never_run_a_configured_external_program() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);
        CommitTwoRevisions(bench.Worktree);

        var extRan      = Path.Combine(bench.Outside, "EXT_RAN");
        var textconvRan = Path.Combine(bench.Outside, "TEXTCONV_RAN");
        var ext      = Script(bench.Outside, "ext.sh", $"touch '{extRan}'");
        var textconv = Script(bench.Outside, "textconv.sh", $"touch '{textconvRan}'\ncat \"$1\"");
        Git(bench.Worktree, "config", "diff.external", ext);
        Git(bench.Worktree, "config", "diff.probe.textconv", textconv);
        File.WriteAllText(Path.Combine(bench.Worktree, ".git", "info", "attributes"), "*.txt diff=probe\n");

        var run = await bench.RunAsync(null,
            new PiScriptedStep(Tool: "git_diff", ArgsJson: """{"base":"HEAD~1","head":"HEAD"}""", Id: "e1"),
            new PiScriptedStep(Tool: "git_show", ArgsJson: """{"rev":"HEAD"}""", Id: "e2"),
            new PiScriptedStep(Tool: "git_log",  ArgsJson: """{"range":"HEAD~1..HEAD"}""", Id: "e3"),
            new PiScriptedStep(Text: "done"));

        await Assert.That(run.ToolResults).Contains(r => r.Contains("+SECOND-LINE"));
        await Assert.That(File.Exists(extRan)).IsFalse();
        await Assert.That(File.Exists(textconvRan)).IsFalse();

        Git(bench.Worktree, "diff", "HEAD~1", "HEAD");
        Git(bench.Worktree, "diff", "--no-ext-diff", "HEAD~1", "HEAD");
        await Assert.That(File.Exists(extRan)).IsTrue();
        await Assert.That(File.Exists(textconvRan)).IsTrue();
    }

    /// <summary>Comparing a revision with checked-out files runs the clean filter the repository's
    /// config names, so a base-only diff must compare with HEAD instead. The direct git call is the
    /// positive control: the same repository runs the filter for a working-tree diff.</summary>
    [Test]
    public async Task A_base_only_diff_never_reads_the_working_tree() {
        RequireGate();
        await using var bench = PiContainmentBench.Create(plantCanaries: false);
        CommitTwoRevisions(bench.Worktree);

        var cleanRan = Path.Combine(bench.Outside, "CLEAN_RAN");
        var clean    = Script(bench.Outside, "clean.sh", $"touch '{cleanRan}'\ncat");
        Git(bench.Worktree, "config", "filter.probe.clean", clean);
        File.WriteAllText(Path.Combine(bench.Worktree, ".git", "info", "attributes"), "*.txt filter=probe\n");
        File.AppendAllText(Path.Combine(bench.Worktree, "inside.txt"), "UNCOMMITTED-LINE\n");

        var run = await bench.RunAsync(null,
            new PiScriptedStep(Tool: "git_diff", ArgsJson: """{"base":"HEAD~1"}""", Id: "w1"),
            new PiScriptedStep(Tool: "git_diff", ArgsJson: """{"base":"HEAD~1..HEAD","path":"inside.txt"}""", Id: "w2"),
            new PiScriptedStep(Text: "done"));

        await Assert.That(run.ToolResults).Contains(r => r.Contains("+SECOND-LINE"));
        await Assert.That(string.Join("\n", run.ToolResults)).DoesNotContain("UNCOMMITTED-LINE");
        await Assert.That(File.Exists(cleanRan)).IsFalse();

        Git(bench.Worktree, "diff", "HEAD");
        await Assert.That(File.Exists(cleanRan)).IsTrue();
    }

    static void CommitTwoRevisions(string worktree) {
        Git(worktree, "init", "-q");
        Git(worktree, "add", "inside.txt");
        Git(worktree, "commit", "-q", "-m", "first revision");
        File.AppendAllText(Path.Combine(worktree, "inside.txt"), "SECOND-LINE\n");
        Git(worktree, "commit", "-q", "-am", "second revision");
    }

    static string Script(string dir, string name, string body) {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    static void Git(string worktree, params string[] args) {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-C", worktree, "-c", "user.name=cert", "-c", "user.email=cert@example.invalid", .. args])
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"]   = "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
    }
}

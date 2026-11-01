using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexCliRunnerTests {
    [TempHome] public required TempHome Home { get; init; }

    /// <summary>A background child keeps stdout open after codex exits; the run must still end at its
    /// deadline with the result codex wrote.</summary>
    [Test]
    public async Task A_descendant_holding_stdout_open_does_not_outlast_the_deadline() {
        Skip.When(OperatingSystem.IsWindows(), "the fake codex is a POSIX shell script");

        using var bin = new TempDir();
        bin.CreateExecutable("codex", """
            #!/bin/sh
            while [ $# -gt 0 ]; do
              if [ "$1" = "--output-last-message" ]; then out="$2"; fi
              shift
            done
            cat > /dev/null
            printf 'ok' > "$out"
            echo '{"type":"turn.completed","usage":{"input_tokens":5,"cached_input_tokens":0,"cache_write_input_tokens":0,"output_tokens":2}}'
            sleep 5 &
            exit 0
            """);

        var started = Environment.TickCount64;
        var outcome = await CodexCliRunner.RunDetailedAsync("irrelevant", TimeSpan.FromSeconds(1), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.Searching(bin.Path)));

        await Assert.That(Environment.TickCount64 - started).IsLessThan(4000);
        await Assert.That(outcome.Result!.Result).IsEqualTo("ok");
        await Assert.That(outcome.Result.OutputTokens).IsEqualTo(2);
    }

    /// <summary>A success carries the usage codex reported, and none when it reported none, so a caller can tell an
    /// unreported count from zero.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_success_carries_the_usage_codex_reported(bool reported) {
        Skip.When(OperatingSystem.IsWindows(), "the fake codex is a POSIX shell script");

        using var bin = new TempDir();
        var usage = reported
            ? """echo '{"type":"turn.completed","usage":{"input_tokens":9,"cached_input_tokens":4,"cache_write_input_tokens":0,"output_tokens":2}}'"""
            : "";
        bin.CreateExecutable("codex", $$"""
            #!/bin/sh
            while [ $# -gt 0 ]; do
              if [ "$1" = "--output-last-message" ]; then out="$2"; fi
              shift
            done
            cat > /dev/null
            printf 'ok' > "$out"
            {{usage}}
            exit 0
            """);

        var outcome = await CodexCliRunner.RunDetailedAsync("irrelevant", TimeSpan.FromSeconds(10), TimeProvider.System, _ => { }, null,
            TestHarnesses.Under(Home, BinaryProbe.Searching(bin.Path)));

        await Assert.That(outcome.Result!.Result).IsEqualTo("ok");
        await Assert.That(outcome.Usage).IsEqualTo(reported ? new Capacitor.Cli.Core.Harness.Claude.ClaudeCliUsage(5, 2, 4, 0, null) : null);
    }

    [Test]
    public async Task BuildCodexArgs_locks_the_run_down_and_reads_the_prompt_from_stdin() {
        var args = CodexCliRunner.BuildCodexArgs("/w/last.txt", null, null, "low");

        await Assert.That(args).IsEquivalentTo(new[] {
            "exec", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check",
            "--sandbox", "read-only",
            "-c", "approval_policy=\"never\"",
            "-c", "sandbox_workspace_write.network_access=false",
            "-c", "web_search=\"disabled\"",
            "--color", "never",
            "--json",
            "-c", "model_reasoning_effort=\"low\"",
            "--output-last-message", "/w/last.txt",
            "-"
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildCodexArgs_passes_the_model_and_schema_file_only_when_given() {
        var args = CodexCliRunner.BuildCodexArgs("/w/last.txt", "/w/schema.json", "gpt-5", "medium");

        await Assert.That(args).Contains("model_reasoning_effort=\"medium\"");
        await Assert.That(args[args.IndexOf("-m") + 1]).IsEqualTo("gpt-5");
        await Assert.That(args[args.IndexOf("--output-schema") + 1]).IsEqualTo("/w/schema.json");
        await Assert.That(args[^1]).IsEqualTo("-");

        var blankModel = CodexCliRunner.BuildCodexArgs("/w/last.txt", null, "  ", "low");
        await Assert.That(blankModel).DoesNotContain("-m");
        await Assert.That(blankModel).DoesNotContain("--output-schema");
    }

    [Test]
    public async Task BuildPrompt_puts_a_delimited_system_prompt_before_the_task() {
        var prompt = CodexCliRunner.BuildPrompt("Summarise this.", "You write titles.");

        await Assert.That(prompt).IsEqualTo("<system_prompt>\nYou write titles.\n</system_prompt>\n\nSummarise this.");
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task BuildPrompt_leaves_the_prompt_alone_without_a_system_prompt(string? systemPrompt) {
        await Assert.That(CodexCliRunner.BuildPrompt("Summarise this.", systemPrompt)).IsEqualTo("Summarise this.");
    }

    [Test]
    public async Task StructuredResult_reserialises_an_object_and_rejects_anything_else() {
        await Assert.That(CodexCliRunner.StructuredResult("{ \"title\": \"hello\", \"n\": 7 }")).IsEqualTo("{ \"title\": \"hello\", \"n\": 7 }");
        await Assert.That(CodexCliRunner.StructuredResult("[1,2]")).IsEqualTo("[1,2]");
        await Assert.That(CodexCliRunner.StructuredResult("\"just text\"")).IsNull();
        await Assert.That(CodexCliRunner.StructuredResult("hello")).IsNull();
    }
}

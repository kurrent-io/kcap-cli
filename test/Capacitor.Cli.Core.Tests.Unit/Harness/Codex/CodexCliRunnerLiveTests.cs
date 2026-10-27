using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// <summary>
/// Runs <see cref="CodexCliRunner"/> against the real, signed-in <c>codex</c> binary. Each test spends
/// real model turns, so the class runs only with <see cref="LiveGateEnvVar"/>=1 and <c>codex</c> on PATH.
/// A wrapper script stands in for <c>codex</c> where a test needs its pid or its exit code; it still
/// runs the real binary. Bare <c>[NotInParallel]</c>: one test pins TMPDIR.
/// </summary>
[NotInParallel]
public class CodexCliRunnerLiveTests {
    const string LiveGateEnvVar = "KCAP_CODEX_LIVE";

    const string TitleSchema =
        """{"type":"object","properties":{"title":{"type":"string"},"n":{"type":"integer"}},"required":["title","n"],"additionalProperties":false}""";

    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [TempHome] public required TempHome Home { get; init; }

    static string RealCodex() {
        Skip.Unless(Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Spends real codex turns: set {LiveGateEnvVar}=1 with a signed-in `codex` on PATH to run");

        var codex = BinaryProbe.FromEnvironment().Resolve("codex");
        Skip.When(codex is null, "codex is not on PATH");
        Skip.When(OperatingSystem.IsWindows(), "the wrapper is a POSIX shell script");

        return codex!;
    }

    HarnessRegistry Real() => TestHarnesses.Under(Home, BinaryProbe.FromEnvironment());

    /// <summary>With nothing to run after it, the wrapper execs codex, so its pid is codex's.</summary>
    HarnessRegistry Wrapped(TempDir bin, string realCodex, string before, string after) {
        var invoke = after.Length == 0 ? $"exec '{realCodex}' \"$@\"" : $"'{realCodex}' \"$@\"\n{after}";
        bin.CreateExecutable("codex", $"#!/bin/sh\n{before}\n{invoke}\n");

        return TestHarnesses.Under(Home, BinaryProbe.Searching(bin.Path));
    }

    static Task<ClaudeCliOutcome> Run(HarnessRegistry harnesses, string prompt, TimeSpan timeout,
        string? jsonSchema = null, string? systemPrompt = null) =>
        CodexCliRunner.RunDetailedAsync(prompt, timeout, TimeProvider.System, _ => { }, null, harnesses,
            jsonSchema: jsonSchema, systemPrompt: systemPrompt);

    [Test]
    public async Task A_schema_is_honoured_and_the_matched_object_comes_back() {
        RealCodex();

        var outcome = await Run(Real(), "Reply with the title \"hello\" and n = 7.", Deadline, jsonSchema: TitleSchema);

        await Assert.That(outcome.Failure).IsNull();
        using var doc = JsonDocument.Parse(outcome.Result!.Result);
        await Assert.That(doc.RootElement.GetProperty("title").GetString()).IsEqualTo("hello");
        await Assert.That(doc.RootElement.GetProperty("n").GetInt32()).IsEqualTo(7);
    }

    [Test]
    public async Task Usage_is_parsed_from_the_event_stream() {
        RealCodex();

        var outcome = await Run(Real(), "Reply with the single word: ok", Deadline);

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.InputTokens + outcome.Result.CacheReadTokens).IsGreaterThan(0);
        await Assert.That(outcome.Result.OutputTokens).IsGreaterThan(0);
        await Assert.That(outcome.Result.Result.Trim('.').ToLowerInvariant()).IsEqualTo("ok");
    }

    [Test]
    public async Task The_system_prompt_preamble_reaches_the_model() {
        RealCodex();
        var codeword = $"kcap-{Guid.NewGuid():N}"[..16];

        var outcome = await Run(Real(), "What is the codeword? Reply with the codeword only.", Deadline,
            systemPrompt: $"The codeword is {codeword}.");

        await Assert.That(outcome.Failure).IsNull();
        await Assert.That(outcome.Result!.Result).Contains(codeword);
    }

    /// <summary>The listener is the evidence, not the model's report: a sandbox that let the command out
    /// would show up as an accepted connection.</summary>
    [Test]
    public async Task The_sandbox_has_no_network() {
        RealCodex();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port     = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();

        const string schema =
            """{"type":"object","properties":{"ran":{"type":"boolean"},"exit_code":{"type":"integer"}},"required":["ran","exit_code"],"additionalProperties":false}""";
        var outcome = await Run(Real(),
            $"Run exactly this shell command once: curl -sS -m 5 http://127.0.0.1:{port}/probe . "
          + "Report whether you ran it and its exit code.",
            Deadline, jsonSchema: schema);

        await Assert.That(outcome.Failure).IsNull();
        using var doc = JsonDocument.Parse(outcome.Result!.Result);
        await Assert.That(doc.RootElement.GetProperty("ran").GetBoolean()).IsTrue();
        await Assert.That(doc.RootElement.GetProperty("exit_code").GetInt32()).IsNotEqualTo(0);
        await Assert.That(accepted.IsCompleted).IsFalse();
    }

    /// <summary>TMPDIR is pinned so the runner's working directory lands where the test can see it.</summary>
    [Test]
    public async Task The_process_is_killed_at_the_timeout_and_the_directory_removed() {
        var codex = RealCodex();
        using var bin  = new TempDir();
        using var temp = new TempDir();
        var pidFile    = temp.PathTo("codex.pid");
        var harnesses  = Wrapped(bin, codex, $"echo $$ > '{pidFile}'", "");
        using var tmpdir = EnvScope.Exclusive("TMPDIR", temp.Path);

        var run = Run(harnesses, "Write a 3000-word essay on the history of the teaspoon.", TimeSpan.FromSeconds(4));

        var pid      = await ReadPidAsync(pidFile);
        var identity = PidIdentity.Capture(pid);
        var outcome  = await run;

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.Timeout);
        await PidIdentity.WaitUntilGoneAsync(pid, identity, TimeSpan.FromSeconds(10));
        await Assert.That(Directory.EnumerateDirectories(temp.Path, "kcap-codex-*")).IsEmpty();
    }

    [Test]
    public async Task A_failure_keeps_the_usage_codex_reported_before_it() {
        var codex = RealCodex();
        using var bin = new TempDir();

        var outcome = await Run(Wrapped(bin, codex, "", "exit 3"), "Reply with the single word: ok", Deadline);

        await Assert.That(outcome.Result).IsNull();
        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.ProcessFailure);
        await Assert.That(outcome.Usage).IsNotNull();
        await Assert.That(outcome.Usage!.OutputTokens).IsGreaterThan(0);
    }

    [Test]
    public async Task A_missing_login_classifies_as_not_signed_in() {
        var codex = RealCodex();
        using var bin       = new TempDir();
        using var codexHome = new TempDir();

        var outcome = await Run(Wrapped(bin, codex, $"export CODEX_HOME='{codexHome.Path}'", ""), "Reply with: ok", Deadline);

        await Assert.That(outcome.Failure).IsEqualTo(ClaudeCliFailure.NotSignedIn);
    }

    [Test]
    public async Task Title_generation_on_a_codex_session_still_returns_plain_text() {
        RealCodex();

        var result = await TitleGeneration.GenerateAsync(
            "Fix the login timeout in the auth service", "I raised the token refresh window and added a retry.",
            TimeProvider.System, _ => { }, null, Real(), vendor: "codex");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Result).IsNotEmpty();
        await Assert.That(result.Result).DoesNotStartWith("{");
        await Assert.That(result.OutputTokens).IsGreaterThan(0);
    }

    static async Task<int> ReadPidAsync(string pidFile) {
        var start = Environment.TickCount64;

        while (Environment.TickCount64 - start < 10_000) {
            if (File.Exists(pidFile) && int.TryParse((await File.ReadAllTextAsync(pidFile)).Trim(), out var pid)) return pid;

            await Task.Delay(20);
        }

        throw new TimeoutException($"the wrapper never wrote {pidFile}");
    }
}

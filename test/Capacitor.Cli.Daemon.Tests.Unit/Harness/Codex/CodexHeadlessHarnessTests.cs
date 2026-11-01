using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>What the server sends for Codex reaches the command line: an empty model leaves codex on its own default,
/// a named one is passed, and a missing reasoning effort runs at low.</summary>
public class CodexHeadlessHarnessTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    async Task<string> ArgsFor(string model, string? effort) {
        Skip.When(OperatingSystem.IsWindows(), "the fake codex is a POSIX shell script");

        var argsFile = Bin.PathTo("args.txt");
        Bin.CreateExecutable("codex", $$"""
            #!/bin/sh
            printf '%s\n' "$@" > '{{argsFile}}'
            while [ $# -gt 0 ]; do
              if [ "$1" = "--output-last-message" ]; then out="$2"; fi
              shift
            done
            cat > /dev/null
            printf 'ok' > "$out"
            exit 0
            """);

        var binaries = BinaryProbe.Searching(Bin.Path);
        var config   = new DaemonConfig {
            Binaries = binaries,
            Profiles = new ProfileContext(new(null, "default", null, null), new ProfileConfig()),
        };
        var harness = new CodexHeadlessHarness(TestHarnesses.Under(Home, binaries), config, TimeProvider.System,
            NullLogger<CodexHeadlessHarness>.Instance);

        var outcome = await harness.RunAsync(
            new RunHeadlessPromptArgs("r1", "codex", model, effort, null, "p", null, 1, null, DateTimeOffset.UtcNow.AddMinutes(1)),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        await Assert.That(outcome.Result?.Result).IsEqualTo("ok");

        return await File.ReadAllTextAsync(argsFile);
    }

    [Test]
    public async Task An_empty_model_leaves_codex_on_its_default() {
        var args = await ArgsFor("", "high");

        await Assert.That(args.Split('\n')).DoesNotContain("-m");
        await Assert.That(args).Contains("model_reasoning_effort=\"high\"");
    }

    [Test]
    public async Task A_named_model_with_no_effort_runs_at_low() {
        var args = await ArgsFor("gpt-5-codex", null);

        await Assert.That(args).Contains("-m\ngpt-5-codex\n");
        await Assert.That(args).Contains("model_reasoning_effort=\"low\"");
    }
}

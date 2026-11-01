using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Claude;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>What reaches the claude command line: a blank schema is no schema, and the CLI is the one the daemon is
/// configured to launch, not whichever claude the search path finds first.</summary>
public class ClaudeHeadlessHarnessTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    string FakeClaude(string relativePath, string mark) =>
        Bin.CreateExecutable(relativePath, $$"""
            #!/bin/sh
            printf '%s\n' '{{mark}}' "$@" > '{{Bin.PathTo("args.txt")}}'
            cat > /dev/null
            printf '{"type":"result","subtype":"success","is_error":false,"result":"ok"}'
            exit 0
            """);

    ClaudeHeadlessHarness Harness(string? claudePath = null) {
        var binaries = BinaryProbe.Searching(Bin.Path);
        var config = new DaemonConfig {
            Binaries = binaries,
            Profiles = new ProfileContext(new(null, "default", null, null), new ProfileConfig()),
        };
        if (claudePath is not null) config.ClaudePath = claudePath;

        return new(TestHarnesses.Under(Home, binaries), config, TimeProvider.System, NullLogger<ClaudeHeadlessHarness>.Instance);
    }

    async Task<string[]> ArgsFor(ClaudeHeadlessHarness harness, string? schema = null) {
        var outcome = await harness.RunAsync(
            new RunHeadlessPromptArgs("r1", "claude", "haiku", null, null, "p", schema, 1, null, DateTimeOffset.UtcNow.AddMinutes(1)),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        await Assert.That(outcome.Result?.Result).IsEqualTo("ok");

        return (await File.ReadAllTextAsync(Bin.PathTo("args.txt"))).Split('\n');
    }

    [Test]
    public async Task A_blank_schema_asks_for_no_structured_reply() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        FakeClaude("claude", "path");

        await Assert.That(await ArgsFor(Harness(), "  ")).DoesNotContain("--json-schema");
    }

    [Test]
    public async Task A_schema_asks_for_a_structured_reply() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        FakeClaude("claude", "path");

        await Assert.That(await ArgsFor(Harness(), "{}")).Contains("--json-schema");
    }

    [Test]
    public async Task The_configured_claude_runs_and_is_advertised_off_the_search_path() {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");
        FakeClaude("claude", "path");
        var configured = FakeClaude("pinned/claude", "configured");
        var harness    = Harness(configured);

        await Assert.That(harness.IsAvailable()).IsTrue();
        await Assert.That((await ArgsFor(harness))[0]).IsEqualTo("configured");
    }

    [Test]
    public async Task A_configured_claude_that_is_gone_is_not_advertised() {
        FakeClaude("claude", "path");

        await Assert.That(Harness(Bin.PathTo("missing/claude")).IsAvailable()).IsFalse();
    }
}

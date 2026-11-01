using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Claude;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>A blank schema is no schema: the runner answers it as text, so claude must not be asked to enforce one.</summary>
public class ClaudeHeadlessHarnessTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    async Task<string> ArgsFor(string? schema) {
        Skip.When(OperatingSystem.IsWindows(), "the fake claude is a POSIX shell script");

        var argsFile = Bin.PathTo("args.txt");
        Bin.CreateExecutable("claude", $$"""
            #!/bin/sh
            printf '%s\n' "$@" > '{{argsFile}}'
            cat > /dev/null
            printf '{"type":"result","subtype":"success","is_error":false,"result":"ok"}'
            exit 0
            """);

        var harness = new ClaudeHeadlessHarness(TestHarnesses.Under(Home, BinaryProbe.Searching(Bin.Path)),
            new ProfileContext(new(null, "default", null, null), new ProfileConfig()), TimeProvider.System,
            NullLogger<ClaudeHeadlessHarness>.Instance);

        var outcome = await harness.RunAsync(
            new RunHeadlessPromptArgs("r1", "claude", "haiku", null, null, "p", schema, 1, null, DateTimeOffset.UtcNow.AddMinutes(1)),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        await Assert.That(outcome.Result?.Result).IsEqualTo("ok");

        return await File.ReadAllTextAsync(argsFile);
    }

    [Test]
    public async Task A_blank_schema_asks_for_no_structured_reply() {
        await Assert.That((await ArgsFor("  ")).Split('\n')).DoesNotContain("--json-schema");
    }

    [Test]
    public async Task A_schema_asks_for_a_structured_reply() {
        await Assert.That((await ArgsFor("{}")).Split('\n')).Contains("--json-schema");
    }
}

using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>Every harness composes its session nudges through <c>SessionNudges</c>. A hook command
/// that called the two emitters itself would skip the session-id line, and nothing else would say so:
/// its output is valid either way.</summary>
public class SessionNudgesCallSiteTests {
    public static IEnumerable<Func<HarnessId>> Harnesses() {
        foreach (var harness in Enum.GetValues<HarnessId>()) yield return () => harness;
    }

    static string HookCommand(HarnessId harness) {
        var path = Path.Combine(RepoTree.Root(), "src", "Capacitor.Cli", "Commands", "Harness", $"{harness}HookCommand.cs");

        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    [Test]
    [MethodDataSource(nameof(Harnesses))]
    public async Task A_hook_command_composes_its_session_nudges_in_one_place(HarnessId harness) {
        var source = HookCommand(harness);

        await Assert.That(source).Contains($"SessionNudges.Resolve(HarnessId.{harness},")
            .Because($"{harness}HookCommand.cs must resolve its session nudges through SessionNudges");
        await Assert.That(source).DoesNotContain("WorkItemsNudgeEmitter.Resolve(");
        await Assert.That(source).DoesNotContain("PlansNudgeEmitter.Resolve(");
    }
}

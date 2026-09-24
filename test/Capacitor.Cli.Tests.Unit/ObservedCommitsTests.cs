using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>A commit's message comes from git, not the transcript, so it is redacted before it
/// leaves the host.</summary>
public class ObservedCommitsTests {
    const string Full = "abc1234def5678abc1234def5678abc1234def56";

    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_message_git_supplies_is_redacted() {
        var observer = new CommitObserver(
            (arguments, _, _) => Task.FromResult(arguments switch {
                "rev-parse --verify --quiet abc1234^{commit}" => Full,
                $"log -1 --format=%s {Full}"                  => "Fix watcher",
                $"log -1 --format=%B {Full}"                  => "Fix watcher\n\nAPI_KEY=sk-live-1234567890abcdef",
                "rev-parse --show-toplevel"                   => Tmp.Path,
                _                                             => null
            }),
            _ => Task.FromResult<(string?, string?)>(("acme", "widgets")));

        var result = new JsonObject {
            ["type"] = "user", ["cwd"] = Tmp.Path,
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject {
                ["type"] = "tool_result", ["tool_use_id"] = "t1", ["content"] = "[main abc1234] Fix watcher" }) }
        }.ToJsonString();

        var observation = ObservedCommits.Claude(observer);
        await observation.ObserveAsync([result]);

        await Assert.That(observation.Pending!.Single().Message).DoesNotContain("sk-live-1234567890abcdef");
    }
}

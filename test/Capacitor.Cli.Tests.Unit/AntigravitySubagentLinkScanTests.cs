using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Antigravity;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravitySubagents.ChildrenOf"/> (AI-1218): the live-watcher
/// scoped scan of a single parent's <c>messages/*.json</c> dir, used to detect subagents that
/// have reported back while the parent conversation is still running (as opposed to
/// <see cref="AntigravitySubagentsTests"/>, which covers the full-history import scan).
/// </summary>
public class AntigravitySubagentLinkScanTests {
    static string NewHome() {
        var home = Path.Combine(Path.GetTempPath(), "kcap-agsublink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    // Writes brain/<owner>/.system_generated/messages/<name>.json with sender→recipient.
    static void WriteMessage(string home, string owner, string name, string sender, string recipient) {
        var dir = Path.Combine(home, ".gemini", "antigravity", "brain", owner, ".system_generated", "messages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"),
            new JsonObject { ["sender"] = sender, ["recipient"] = recipient }.ToJsonString());
    }

    [Test]
    public async Task ChildrenOf_returns_only_children_reporting_to_the_parent_deduped() {
        var home = NewHome();
        try {
            // Two children report back to the parent.
            WriteMessage(home, owner: "P", name: "m1", sender: "child1", recipient: "P");
            WriteMessage(home, owner: "P", name: "m2", sender: "child2", recipient: "P");
            // A duplicate message from child1 (e.g. re-sent) must not double the result.
            WriteMessage(home, owner: "P", name: "m3", sender: "child1", recipient: "P");
            // Unrelated: a message in the parent's dir addressed to someone else must be excluded.
            WriteMessage(home, owner: "P", name: "m4", sender: "child3", recipient: "someone-else");
            // Malformed file must be skipped, not throw.
            var dir = Path.Combine(home, ".gemini", "antigravity", "brain", "P", ".system_generated", "messages");
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");

            var children = AntigravitySubagents.ChildrenOf("P", home: home, geminiCliHome: "");

            await Assert.That(children.Count).IsEqualTo(2);
            await Assert.That(children.OrderBy(x => x).ToList()).IsEquivalentTo(new List<string> { "child1", "child2" });
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task ChildrenOf_excludes_self_messages() {
        var home = NewHome();
        try {
            WriteMessage(home, owner: "P", name: "self", sender: "P", recipient: "P");

            var children = AntigravitySubagents.ChildrenOf("P", home: home, geminiCliHome: "");
            await Assert.That(children.Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task ChildrenOf_is_empty_when_no_messages_dir() {
        var home = NewHome();
        try {
            var children = AntigravitySubagents.ChildrenOf("P", home: home, geminiCliHome: "");
            await Assert.That(children.Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task ChildrenOf_honors_cancellation() {
        var home = NewHome();
        try {
            // A handful of message files so the per-file loop actually runs.
            for (var i = 0; i < 5; i++)
                WriteMessage(home, owner: "P", name: $"m{i}", sender: $"child{i}", recipient: "P");

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.That(() => AntigravitySubagents.ChildrenOf("P", home: home, geminiCliHome: "", ct: cts.Token))
                .Throws<OperationCanceledException>();
        } finally { Directory.Delete(home, recursive: true); }
    }
}

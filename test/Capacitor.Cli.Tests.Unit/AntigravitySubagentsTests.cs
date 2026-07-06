using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Antigravity;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravitySubagents"/> (AI-1160): the child→parent map built
/// from the parent brain dir's <c>messages/*.json</c> linkage used to nest subagents at
/// import time.
/// </summary>
public class AntigravitySubagentsTests {
    static string NewHome() {
        var home = Path.Combine(Path.GetTempPath(), "kcap-agsub-" + Guid.NewGuid().ToString("N"));
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

    static void MakeBrainDir(string home, string convId) =>
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity", "brain", convId, ".system_generated", "logs"));

    // AI-1160 review (finding 3): the discovery scan is O(history) IO and must be interruptible.
    [Test]
    public async Task BuildParentMap_honors_cancellation() {
        var home = NewHome();
        try {
            MakeBrainDir(home, "P"); // at least one brain dir so the scan loop runs
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.That(() => AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "", ct: cts.Token))
                .Throws<OperationCanceledException>();
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task BuildParentMap_links_children_to_the_parent_that_owns_the_message() {
        var home = NewHome();
        try {
            // Parent P owns messages from two children C1, C2 (child = sender, parent = recipient).
            MakeBrainDir(home, "P"); MakeBrainDir(home, "C1"); MakeBrainDir(home, "C2");
            WriteMessage(home, owner: "P", name: "m1", sender: "C1", recipient: "P");
            WriteMessage(home, owner: "P", name: "m2", sender: "C2", recipient: "P");

            var map = AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "");

            await Assert.That(map.Count).IsEqualTo(2);
            await Assert.That(map["C1"]).IsEqualTo("P");
            await Assert.That(map["C2"]).IsEqualTo("P");
            // The root is never a sender, so it isn't a key.
            await Assert.That(map.ContainsKey("P")).IsFalse();

            await Assert.That(AntigravitySubagents.ResolveParent("C1", home: home, geminiCliHome: "")).IsEqualTo("P");
            await Assert.That(AntigravitySubagents.ResolveParent("P", home: home, geminiCliHome: "")).IsNull();
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task BuildParentMap_ignores_self_messages_and_malformed_files() {
        var home = NewHome();
        try {
            MakeBrainDir(home, "P");
            WriteMessage(home, owner: "P", name: "self", sender: "P", recipient: "P"); // self → ignored
            // malformed file
            var dir = Path.Combine(home, ".gemini", "antigravity", "brain", "P", ".system_generated", "messages");
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");

            var map = AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "");
            await Assert.That(map.Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task BuildParentMap_is_empty_when_no_antigravity_data() {
        var home = NewHome();
        try {
            await Assert.That(AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "").Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task BuildParentMap_is_deterministic_when_a_child_maps_to_multiple_parents() {
        var home = NewHome();
        try {
            // Pathological non-tree data: C reports to both "Pz" and "Pa". The lexicographically
            // smallest recipient wins regardless of which message file is enumerated first.
            MakeBrainDir(home, "Pa"); MakeBrainDir(home, "Pz"); MakeBrainDir(home, "C");
            WriteMessage(home, owner: "Pz", name: "m1", sender: "C", recipient: "Pz");
            WriteMessage(home, owner: "Pa", name: "m2", sender: "C", recipient: "Pa");

            var map = AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "");
            await Assert.That(map["C"]).IsEqualTo("Pa");
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task ResolveTopLevelAncestor_walks_a_deep_chain_to_the_root() {
        // P ← C ← G : the grandchild resolves to P, not C.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["C"] = "P", ["G"] = "C" };
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("G", map)).IsEqualTo("P");
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("C", map)).IsEqualTo("P");
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("P", map)).IsEqualTo("P");
        // An unknown/unlinked conversation is its own root.
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("X", map)).IsEqualTo("X");
    }

    [Test]
    public async Task ResolveTopLevelAncestor_is_cycle_safe() {
        // A ↔ B cycle: each member resolves to itself rather than looping forever / vanishing.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "B", ["B"] = "A" };
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("A", map)).IsEqualTo("A");
        await Assert.That(AntigravitySubagents.ResolveTopLevelAncestor("B", map)).IsEqualTo("B");
    }

    [Test]
    public async Task BuildRootDescendants_groups_transitive_descendants_under_each_root() {
        // Tree: P ← C ← G, and standalone S. Root P owns {C, G}; S owns nothing.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["C"] = "P", ["G"] = "C" };
        var byRoot = AntigravitySubagents.BuildRootDescendants(new[] { "P", "C", "G", "S" }, map);

        await Assert.That(byRoot.ContainsKey("P")).IsTrue();
        await Assert.That(byRoot["P"].OrderBy(x => x).ToList()).IsEquivalentTo(new List<string> { "C", "G" });
        await Assert.That(byRoot.ContainsKey("S")).IsTrue();
        await Assert.That(byRoot["S"].Count).IsEqualTo(0);
        // Children are NOT keys (they import under their root, not as sessions).
        await Assert.That(byRoot.ContainsKey("C")).IsFalse();
        await Assert.That(byRoot.ContainsKey("G")).IsFalse();
    }

    [Test]
    public async Task BuildRootDescendants_imports_cycle_members_standalone() {
        // A ↔ B cycle plus a real root R with child C. Cycle members each become their own root
        // (with no descendants) rather than being lost.
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["A"] = "B", ["B"] = "A", ["C"] = "R" };
        var byRoot = AntigravitySubagents.BuildRootDescendants(["A", "B", "R", "C"], map);

        await Assert.That(byRoot.ContainsKey("A")).IsTrue();
        await Assert.That(byRoot["A"].Count).IsEqualTo(0);
        await Assert.That(byRoot.ContainsKey("B")).IsTrue();
        await Assert.That(byRoot["B"].Count).IsEqualTo(0);
        await Assert.That(byRoot["R"]).IsEquivalentTo((List<string>)["C"]);
    }
}

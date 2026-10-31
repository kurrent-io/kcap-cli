using TUnit.Assertions.Enums;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>What <c>kcap import</c> says about what it could not use: which folders matched no
/// repository, what it skipped and which setting skipped it.</summary>
public class ImportDiagnosticsTests {
    static readonly IReadOnlyList<ImportDiscoveryWindow> Everything = [new(FirstRunImportWindows.Everything, null)];

    [Test]
    public async Task The_count_is_of_folders_not_repositories() {
        await Assert.That(ImportCommand.MatchingFoldersLine(4)).IsEqualTo("Matching 4 folders to repositories...");
        await Assert.That(ImportCommand.MatchingFoldersLine(1)).IsEqualTo("Matching 1 folder to repositories...");
    }

    [Test]
    public async Task Unmatched_sessions_are_named_by_folder_most_first() {
        var summary = ImportDiscoverySummary.Build(
            [("a", null), ("b", null), ("c", null), ("d", null)],
            new Dictionary<string, (string Owner, string Name)?> { ["a"] = ("acme", "shop"), ["b"] = null, ["c"] = null, ["d"] = null },
            Everything,
            folderBySession: new Dictionary<string, string> { ["a"] = "/w/shop", ["b"] = "/w/scratch", ["c"] = "/w/scratch" });

        await Assert.That(summary.UnmatchedFolders).IsEquivalentTo(
            new[] { new ImportUnmatchedFolder("/w/scratch", 2), new ImportUnmatchedFolder(null, 1) }, CollectionOrdering.Matching);

        var text = ImportDiscoveryRender.ToText(summary);

        await Assert.That(text).Contains("couldn't match to a repository: 3");
        await Assert.That(text).Contains("    /w/scratch  (2 sessions)");
        await Assert.That(text).Contains("    no folder recorded  (1 session)");
        await Assert.That(text).Contains("kcap remap <from> <to>");
    }

    /// <summary>The json report names each folder in its own list, so a caller can offer it as a choice,
    /// and leaves the repository list and the unmatched total as they were.</summary>
    [Test]
    public async Task The_json_report_lists_the_folders_apart_from_the_repositories() {
        var summary = ImportDiscoverySummary.Build(
            [("b", null), ("c", null)], new Dictionary<string, (string Owner, string Name)?> { ["b"] = null, ["c"] = null }, Everything,
            folderBySession: new Dictionary<string, string> { ["b"] = "/w/scratch" });

        var root    = System.Text.Json.Nodes.JsonNode.Parse(ImportDiscoveryRender.ToJson(summary))!;
        var folders = root["unmatched_folders"]!.AsArray();

        await Assert.That(root["repos"]!.AsArray()).IsEmpty();
        await Assert.That(root["unmatched_sessions"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(folders[0]!["folder"]!.GetValue<string>()).IsEqualTo("/w/scratch");
        await Assert.That(folders[0]!["windows"]!.AsArray()[0]!["sessions"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(folders[1]!["folder"]).IsNull();
        await Assert.That(folders[1]!["sessions"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Past_ten_folders_the_rest_are_counted() {
        var lines = ImportDiscoveryRender.UnmatchedFolderLines(
            [.. Enumerable.Range(1, 12).Select(i => new ImportUnmatchedFolder($"/w/f{i:00}", 1))]).ToList();

        await Assert.That(lines).Contains("    /w/f10  (1 session)");
        await Assert.That(lines).DoesNotContain("    /w/f11  (1 session)");
        await Assert.That(lines).Contains("    ... and 2 more folders (2 sessions)");
    }

    [Test]
    public async Task An_auto_skip_names_each_excluded_source_and_its_setting() {
        string[] lines = [.. ImportCommand.ExcludedSourceLines([("acme/secret", 2)], [("/code/private", 1)], 3)];

        await Assert.That(lines).IsEquivalentTo(new[] {
            "repository acme/secret: 2 sessions, excluded by the profile's excluded_repos",
            "path /code/private: 1 session, excluded by `kcap ignore`",
            "3 sessions outside the allow list (`kcap allow --list`)",
        }, CollectionOrdering.Matching);
    }
}
